// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Tracing.Stacks.Formats;

namespace Microsoft.Diagnostics.Tools.Trace
{
    /// <summary>
    /// Adds the 'json' format. Lives in its own file so the upstream converter only needs the 'partial' keyword,
    /// the enum value with its extension and the call to <see cref="WriteJson"/>.
    /// </summary>
    internal static partial class TraceFileFormatConverter
    {
        private sealed class Frame
        {
            public string Name;
            public bool IsThread;
            public bool IsScheduling;
            public int Index = -1;
        }

        /// <summary>
        /// Writes a speedscope document (https://www.speedscope.app/file-format-schema.json) with these profiles, in this order:
        /// 'CPU (all threads)': the CPU samples of every thread in one 'sampled' profile weighted in milliseconds. The threads of an application share
        /// their work, async code hops between them, so a single thread rarely shows the hot path. Only samples taken in managed code are included,
        /// EventPipe cannot tell whether a thread inside native code is running or blocked.
        /// 'Allocations': the GCAllocationTick events (verbose GC events, e.g. the 'gc-verbose' profile) as a 'sampled' profile weighted in bytes, with the
        /// allocated type as the leaf frame. The runtime raises the event about once per 100 KB allocated, so the weights are an estimate and not an exact
        /// count, and a type backed by a handful of samples is not evidence.
        /// One profile per thread, written by TraceEvent like the 'speedscope' format: the wall clock timeline of the thread including the time it is blocked.
        /// The stacks of the first two profiles start at the first frame that is not scheduling code of the runtime (thread pool, timers, task continuations).
        /// These frames only tell how the code got onto its thread, and they split the work of one method between several roots.
        /// </summary>
        private static void WriteJson(TraceLog eventLog, SymbolReader symbolReader, MutableTraceEventStackSource defaultStackSource, string outputFilename)
        {
            // The thread time comes from the samples of the profiler only. By default the computer takes every event with a stack for a sample,
            // so an allocation followed by a wait turns the wait into CPU time: 8.6 s were reported for a process that used 5.4 s
            MutableTraceEventStackSource stackSource = new(eventLog) { OnlyManagedCodeStacks = true };
            SampleProfilerThreadTimeComputer computer = new(eventLog, symbolReader) { IncludeEventSourceEvents = false };
            computer.GenerateThreadTimeStacks(stackSource, eventLog.Events.Filter(traceEvent => traceEvent.ProviderName == SampleProfilerTraceEventParser.ProviderName));

            bool hasCpuSamples = stackSource.SampleIndexLimit > 0;
            bool hasAllocations = eventLog.Events.ByEventType<GCAllocationTickTraceData>().Any();
            if (!hasCpuSamples && !hasAllocations)
            {
                SpeedScopeStackSourceWriter.WriteStackViewAsJson(defaultStackSource, outputFilename);
                return;
            }

            // TraceEvent can only write its document to a file, so the threads are read back and written again next to the other profiles
            if (hasCpuSamples)
            {
                SpeedScopeStackSourceWriter.WriteStackViewAsJson(stackSource, outputFilename);
            }
            using JsonDocument threads = hasCpuSamples ? JsonDocument.Parse(File.ReadAllBytes(outputFilename)) : null;

            // Frames are shared by name, like TraceEvent does: a method that was jitted more than once has several frame indexes but must stay one frame.
            // The thread frames only carry a name and keep their positions, so the thread profiles stay valid
            List<string> frames = new();
            Dictionary<string, int> frameIndexes = new();
            if (threads != null)
            {
                foreach (JsonElement frame in threads.RootElement.GetProperty("shared").GetProperty("frames").EnumerateArray())
                {
                    frameIndexes.TryAdd(frame.GetProperty("name").GetString(), frames.Count);
                    frames.Add(frame.GetProperty("name").GetString());
                }
            }

            Dictionary<StackSourceFrameIndex, Frame> knownFrames = new();
            Frame GetFrame(StackSourceFrameIndex frameIndex)
            {
                if (!knownFrames.TryGetValue(frameIndex, out Frame frame))
                {
                    string name = stackSource.GetFrameName(frameIndex, false);
                    knownFrames.Add(frameIndex, frame = new Frame
                    {
                        Name = name,
                        IsThread = name.StartsWith("Thread (", StringComparison.Ordinal),
                        IsScheduling = name.StartsWith("System.Private.CoreLib!System.Threading.", StringComparison.Ordinal)
                            || name.StartsWith("System.Private.CoreLib!System.Runtime.CompilerServices.", StringComparison.Ordinal)
                    });
                }
                return frame;
            }
            int GetFrameIndex(string name)
            {
                if (!frameIndexes.TryGetValue(name, out int index))
                {
                    frameIndexes.Add(name, index = frames.Count);
                    frames.Add(name);
                }
                return index;
            }
            // From the root to the leaf, without the process and thread frames the stack source puts in front of every stack
            List<int> GetStack(StackSourceCallStackIndex callStack)
            {
                List<Frame> stack = new();
                for (; callStack != StackSourceCallStackIndex.Invalid; callStack = stackSource.GetCallerIndex(callStack))
                {
                    Frame frame = GetFrame(stackSource.GetFrameIndex(callStack));
                    if (frame.IsThread)
                    {
                        break;
                    }
                    stack.Add(frame);
                }
                stack.Reverse();

                // A stack made of scheduling code only, e.g. a pool thread waiting for work, is kept as it is
                int scheduling = stack.TakeWhile(frame => frame.IsScheduling).Count();
                List<int> result = new();
                foreach (Frame frame in stack.Skip(scheduling < stack.Count ? scheduling : 0))
                {
                    if (frame.Index < 0)
                    {
                        frame.Index = GetFrameIndex(frame.Name);
                    }
                    result.Add(frame.Index);
                }
                return result;
            }

            List<int[]> cpuSamples = new();
            List<double> cpuWeights = new();
            if (hasCpuSamples)
            {
                stackSource.ForEach(sample =>
                {
                    // The leaf of every thread time stack tells what the thread was doing, it is not a frame of the code
                    List<int> stack = GetFrame(stackSource.GetFrameIndex(sample.StackIndex)).Name == "CPU_TIME" ? GetStack(stackSource.GetCallerIndex(sample.StackIndex)) : null;
                    // The profiler stops a thread at its next suspension poll. The poll helper on top of the stack is where the thread was parked,
                    // the time belongs to the method that polled
                    if (stack?.Count > 1 && frames[stack[^1]].Contains("Thread.<PollGC>", StringComparison.Ordinal))
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }
                    if (stack?.Count > 0)
                    {
                        cpuSamples.Add(stack.ToArray());
                        cpuWeights.Add(Math.Round(sample.Metric, 4));
                    }
                });
            }

            List<int[]> allocationSamples = new();
            List<double> allocationWeights = new();
            foreach (GCAllocationTickTraceData data in eventLog.Events.ByEventType<GCAllocationTickTraceData>())
            {
                List<int> stack = GetStack(stackSource.GetCallStack(data.CallStackIndex(), data));
                stack.Add(GetFrameIndex($"{(string.IsNullOrEmpty(data.TypeName) ? "UNKNOWN" : data.TypeName)} ({data.AllocationKind})"));
                allocationSamples.Add(stack.ToArray());
                allocationWeights.Add(data.AllocationAmount64);
            }

            using FileStream stream = File.Create(outputFilename);
            using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { SkipValidation = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            void WriteProfile(string name, string unit, List<int[]> samples, List<double> weights)
            {
                if (samples.Count == 0)
                {
                    return;
                }
                writer.WriteStartObject();
                writer.WriteString("type", "sampled");
                writer.WriteString("name", name);
                writer.WriteString("unit", unit);
                writer.WriteNumber("startValue", 0);
                writer.WriteNumber("endValue", weights.Sum());
                writer.WriteStartArray("samples");
                for (int i = 0; i < samples.Count; i++)
                {
                    writer.WriteStartArray();
                    foreach (int frame in samples[i])
                    {
                        writer.WriteNumberValue(frame);
                    }
                    writer.WriteEndArray();
                    if (i % 4096 == 0)
                    {
                        writer.Flush(); // Keep the buffer of the writer small on long traces
                    }
                }
                writer.WriteEndArray();
                writer.WriteStartArray("weights");
                foreach (double weight in weights)
                {
                    writer.WriteNumberValue(weight);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteStartObject();
            writer.WriteString("$schema", "https://www.speedscope.app/file-format-schema.json");
            writer.WriteString("name", Path.GetFileName(outputFilename));
            writer.WriteString("exporter", "dotnet-trace");

            writer.WriteStartObject("shared");
            writer.WriteStartArray("frames");
            foreach (string frame in frames)
            {
                writer.WriteStartObject();
                writer.WriteString("name", frame);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartArray("profiles");
            WriteProfile("CPU (all threads)", "milliseconds", cpuSamples, cpuWeights);
            WriteProfile("Allocations (estimate, ~100 KB per sample)", "bytes", allocationSamples, allocationWeights);
            if (threads != null)
            {
                foreach (JsonElement profile in threads.RootElement.GetProperty("profiles").EnumerateArray())
                {
                    profile.WriteTo(writer);
                    writer.Flush();
                }
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
    }
}
