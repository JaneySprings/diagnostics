// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Graphs;

namespace Microsoft.Diagnostics.Tools.GCDump
{
    internal enum GCDumpFileFormat { GCDump = 1, Json };

    internal static class GCDumpFileFormatConverter
    {
        private static readonly IReadOnlyDictionary<GCDumpFileFormat, string> GCDumpFileFormatExtensions = new Dictionary<GCDumpFileFormat, string>() {
            { GCDumpFileFormat.GCDump,  "gcdump" },
            { GCDumpFileFormat.Json,    "gcdump.json" }
        };

        // The first 8 bytes of a nettrace file are the ASCII string "Nettrace"
        private static readonly byte[] NetTraceHeader = [0x4E, 0x65, 0x74, 0x74, 0x72, 0x61, 0x63, 0x65];

        internal static string GetConvertedFilename(string fileToConvert, string outputfile, GCDumpFileFormat format)
        {
            if (string.IsNullOrWhiteSpace(outputfile))
            {
                outputfile = fileToConvert;
            }

            string extension = "." + GCDumpFileFormatExtensions[format];
            if (outputfile.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return outputfile;
            }

            // Only the last extension is replaced, so 'foo.gcdump' and 'foo.nettrace' both become 'foo.gcdump.json'
            return Path.ChangeExtension(outputfile, extension);
        }

        internal static bool IsNetTraceFile(string filename)
        {
            using FileStream fs = new(filename, FileMode.Open, FileAccess.Read);
            Span<byte> header = stackalloc byte[NetTraceHeader.Length];
            Span<byte> readBuffer = header;
            int bytesRead;
            while (readBuffer.Length > 0 && (bytesRead = fs.Read(readBuffer)) > 0)
            {
                readBuffer = readBuffer.Slice(bytesRead);
            }

            return readBuffer.Length == 0 && header.SequenceEqual(NetTraceHeader);
        }

        internal static void ConvertToFormat(TextWriter stdOut, GCDumpFileFormat format, GCHeapDump dump, string outputFilename)
        {
            switch (format)
            {
                case GCDumpFileFormat.Json:
                    stdOut.WriteLine($"Writing {format} file '{outputFilename}'...");
                    WriteJson(dump, outputFilename);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(format), $"Cannot convert a gcdump to the {format} format.");
            }

            stdOut.WriteLine("Conversion complete");
        }

        /// <summary>
        /// Writes the heap graph as a single JSON document laid out in flat arrays so it stays compact for large heaps:
        ///   nodes         - [typeIndex, size, childCount] triplets, one per node
        ///   edges         - child node indexes; the children of node N follow the children of node N-1
        ///   addresses     - object address per node (0 for the synthetic root nodes)
        ///   dominators    - immediate dominator per node, -1 for the root and for nodes unreachable from it
        ///   retainedSizes - size of the node plus everything it dominates
        ///   gcSegments    - the GC generation ranges (used part) recorded with the dump, when available
        /// </summary>
        private static void WriteJson(GCHeapDump dump, string outputFilename)
        {
            MemoryGraph graph = dump.MemoryGraph;
            int nodeCount = (int)graph.NodeIndexLimit;
            Node nodeStorage = graph.AllocNodeStorage();

            // Flatten the graph into compressed sparse row form so it can be traversed without going through the graph reader again.
            // TotalNumberOfReferences is not persisted in .gcdump files, so the edge count is computed with a first pass.
            int[] typeIndexes = new int[nodeCount];
            int[] sizes = new int[nodeCount];
            int[] edgeStart = new int[nodeCount + 1];
            for (int i = 0; i < nodeCount; i++)
            {
                Node node = graph.GetNode((NodeIndex)i, nodeStorage);
                typeIndexes[i] = (int)node.TypeIndex;
                sizes[i] = node.Size;
                edgeStart[i + 1] = edgeStart[i] + node.ChildCount;
            }

            int[] edges = new int[edgeStart[nodeCount]];
            for (int i = 0; i < nodeCount; i++)
            {
                Node node = graph.GetNode((NodeIndex)i, nodeStorage);
                int edge = edgeStart[i];
                for (NodeIndex childIndex = node.GetFirstChildIndex(); childIndex != NodeIndex.Invalid; childIndex = node.GetNextChildIndex())
                {
                    edges[edge++] = (int)childIndex;
                }
            }

            int[] dominators = ComputeDominators((int)graph.RootIndex, edgeStart, edges, out int[] postOrder);
            long[] retainedSizes = ComputeRetainedSizes(sizes, dominators, postOrder);

            using FileStream stream = File.Create(outputFilename);
            using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { SkipValidation = true });

            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("creationTool", dump.CreationTool);
            if (dump.TimeCollected != default)
            {
                writer.WriteString("timeCollected", dump.TimeCollected);
            }
            writer.WriteString("machineName", dump.MachineName);
            writer.WriteString("processName", dump.ProcessName);
            writer.WriteNumber("processId", dump.ProcessID);
            writer.WriteNumber("totalProcessCommit", dump.TotalProcessCommit);
            writer.WriteNumber("totalProcessWorkingSet", dump.TotalProcessWorkingSet);
            writer.WriteBoolean("is64Bit", graph.Is64Bit);
            writer.WriteNumber("totalSize", graph.TotalSize);
            writer.WriteNumber("rootIndex", (int)graph.RootIndex);

            List<GCHeapDumpSegment> segments = dump.DotNetHeapInfo?.Segments;
            if (segments != null && segments.Count > 0)
            {
                long gcHeapSize = 0;
                foreach (GCHeapDumpSegment segment in segments)
                {
                    gcHeapSize += (long)(segment.End - segment.Start);
                }
                writer.WriteNumber("gcHeapSize", gcHeapSize);
                writer.WriteStartArray("gcSegments");
                foreach (GCHeapDumpSegment segment in segments)
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("start", segment.Start);
                    writer.WriteNumber("end", segment.End);
                    writer.WriteNumber("gen0End", segment.Gen0End);
                    writer.WriteNumber("gen1End", segment.Gen1End);
                    writer.WriteNumber("gen2End", segment.Gen2End);
                    writer.WriteNumber("gen3End", segment.Gen3End);
                    writer.WriteNumber("gen4End", segment.Gen4End);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            writer.WriteStartArray("types");
            NodeType typeStorage = graph.AllocTypeNodeStorage();
            for (NodeTypeIndex typeIndex = 0; typeIndex < graph.NodeTypeIndexLimit; typeIndex++)
            {
                NodeType type = graph.GetType(typeIndex, typeStorage);
                writer.WriteStartObject();
                writer.WriteString("name", type.Name);
                if (!string.IsNullOrEmpty(type.ModuleName))
                {
                    writer.WriteString("module", type.ModuleName);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("nodes");
            for (int i = 0; i < nodeCount; i++)
            {
                writer.WriteNumberValue(typeIndexes[i]);
                writer.WriteNumberValue(sizes[i]);
                writer.WriteNumberValue(edgeStart[i + 1] - edgeStart[i]);
                FlushPeriodically(writer, i);
            }
            writer.WriteEndArray();

            writer.WriteStartArray("edges");
            for (int i = 0; i < edges.Length; i++)
            {
                writer.WriteNumberValue(edges[i]);
                FlushPeriodically(writer, i);
            }
            writer.WriteEndArray();

            // Addresses are written as plain numbers. User-mode addresses fit in 53 bits, so JavaScript readers keep them exact.
            writer.WriteStartArray("addresses");
            for (int i = 0; i < nodeCount; i++)
            {
                writer.WriteNumberValue(graph.GetAddress((NodeIndex)i));
                FlushPeriodically(writer, i);
            }
            writer.WriteEndArray();

            writer.WriteStartArray("dominators");
            for (int i = 0; i < nodeCount; i++)
            {
                writer.WriteNumberValue(dominators[i]);
                FlushPeriodically(writer, i);
            }
            writer.WriteEndArray();

            writer.WriteStartArray("retainedSizes");
            for (int i = 0; i < nodeCount; i++)
            {
                writer.WriteNumberValue(retainedSizes[i]);
                FlushPeriodically(writer, i);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        private static void FlushPeriodically(Utf8JsonWriter writer, int counter)
        {
            if ((counter & 0xFFFF) == 0)
            {
                writer.Flush();
            }
        }

        /// <summary>
        /// Computes the immediate dominator of every node with the iterative algorithm from
        /// Cooper, Harvey and Kennedy, "A Simple, Fast Dominance Algorithm".
        /// A synthetic node above the real root adopts every node that is unreachable from it,
        /// so the result for those nodes (and for the root itself) is -1.
        /// 'postOrder' receives the nodes in DFS post-order, followed by the synthetic root.
        /// </summary>
        private static int[] ComputeDominators(int rootIndex, int[] edgeStart, int[] edges, out int[] postOrder)
        {
            int nodeCount = edgeStart.Length - 1;
            int virtualRoot = nodeCount;

            // Post-order numbering with an explicit stack; a recursive walk overflows on long reference chains.
            int[] postOrderIndex = new int[nodeCount + 1];
            int[] order = new int[nodeCount + 1];
            Array.Fill(postOrderIndex, -1);
            int[] nextEdge = new int[nodeCount];
            Array.Copy(edgeStart, nextEdge, nodeCount);
            int[] stack = new int[nodeCount];
            bool[] isTreeRoot = new bool[nodeCount];
            int visited = 0;

            void Visit(int start)
            {
                if (postOrderIndex[start] != -1)
                {
                    return;
                }

                isTreeRoot[start] = true;
                postOrderIndex[start] = -2;
                int stackTop = 0;
                stack[stackTop++] = start;
                while (stackTop > 0)
                {
                    int node = stack[stackTop - 1];
                    int edge = nextEdge[node];
                    if (edge < edgeStart[node + 1])
                    {
                        nextEdge[node] = edge + 1;
                        int child = edges[edge];
                        if (postOrderIndex[child] == -1)
                        {
                            postOrderIndex[child] = -2;
                            stack[stackTop++] = child;
                        }
                    }
                    else
                    {
                        stackTop--;
                        postOrderIndex[node] = visited;
                        order[visited++] = node;
                    }
                }
            }

            Visit(rootIndex);
            for (int i = 0; i < nodeCount; i++)
            {
                Visit(i);
            }
            postOrderIndex[virtualRoot] = visited;
            order[visited++] = virtualRoot;
            postOrder = order;

            // Reverse edges, again in compressed sparse row form.
            int[] predStart = new int[nodeCount + 1];
            for (int i = 0; i < edges.Length; i++)
            {
                predStart[edges[i] + 1]++;
            }
            for (int i = 0; i < nodeCount; i++)
            {
                predStart[i + 1] += predStart[i];
            }
            int[] preds = new int[edges.Length];
            int[] predFill = new int[nodeCount];
            Array.Copy(predStart, predFill, nodeCount);
            for (int i = 0; i < nodeCount; i++)
            {
                for (int edge = edgeStart[i]; edge < edgeStart[i + 1]; edge++)
                {
                    preds[predFill[edges[edge]]++] = i;
                }
            }

            int[] idom = new int[nodeCount + 1];
            Array.Fill(idom, -1);
            idom[virtualRoot] = virtualRoot;
            for (int i = 0; i < nodeCount; i++)
            {
                if (isTreeRoot[i])
                {
                    idom[i] = virtualRoot;
                }
            }

            int Intersect(int a, int b)
            {
                while (a != b)
                {
                    while (postOrderIndex[a] < postOrderIndex[b])
                    {
                        a = idom[a];
                    }
                    while (postOrderIndex[b] < postOrderIndex[a])
                    {
                        b = idom[b];
                    }
                }
                return a;
            }

            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int position = nodeCount - 1; position >= 0; position--)
                {
                    int node = order[position];
                    if (isTreeRoot[node])
                    {
                        continue;
                    }

                    int newIdom = -1;
                    for (int pred = predStart[node]; pred < predStart[node + 1]; pred++)
                    {
                        int predecessor = preds[pred];
                        if (idom[predecessor] == -1)
                        {
                            continue;
                        }

                        newIdom = newIdom == -1 ? predecessor : Intersect(predecessor, newIdom);
                    }

                    if (newIdom != -1 && newIdom != idom[node])
                    {
                        idom[node] = newIdom;
                        changed = true;
                    }
                }
            }

            int[] dominators = new int[nodeCount];
            for (int i = 0; i < nodeCount; i++)
            {
                dominators[i] = idom[i] == virtualRoot ? -1 : idom[i];
            }
            return dominators;
        }

        private static long[] ComputeRetainedSizes(int[] sizes, int[] dominators, int[] postOrder)
        {
            long[] retainedSizes = new long[sizes.Length];
            for (int i = 0; i < sizes.Length; i++)
            {
                retainedSizes[i] = sizes[i];
            }

            // A node's dominator is a proper ancestor in the DFS tree, so walking in post-order visits every dominated node first.
            for (int order = 0; order < sizes.Length; order++)
            {
                int node = postOrder[order];
                if (dominators[node] != -1)
                {
                    retainedSizes[dominators[node]] += retainedSizes[node];
                }
            }
            return retainedSizes;
        }
    }
}
