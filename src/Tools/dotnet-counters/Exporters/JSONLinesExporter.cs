// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Text.Json;
using Microsoft.Diagnostics.Monitoring.EventPipe;

namespace Microsoft.Diagnostics.Tools.Counters.Exporters
{
    /// <summary>
    /// Streams every counter payload to stdout as one JSON object per line, so a host can consume the values while
    /// the session is still running. The 'json' format cannot be read live: its file only becomes valid JSON in Stop().
    /// </summary>
    internal sealed class JSONLinesExporter : ICounterRenderer
    {
        private readonly Stream _output = Console.OpenStandardOutput();

        public void Initialize() { }

        public void EventPipeSourceConnected() { }

        public void ToggleStatus(bool paused) { }

        // Keep stdout machine-readable, diagnostics go to stderr
        public void SetErrorText(string errorText) => Console.Error.WriteLine(errorText);

        // CounterMonitor serializes the calls, so the stream needs no lock of its own
        public void CounterPayloadReceived(CounterPayload payload, bool _)
        {
            using (Utf8JsonWriter writer = new(_output))
            {
                writer.WriteStartObject();
                writer.WriteString("timestamp", payload.Timestamp);
                writer.WriteString("provider", payload.CounterMetadata.ProviderName);
                writer.WriteString("name", payload.CounterMetadata.CounterName);
                writer.WriteString("displayName", payload.GetDisplay());
                writer.WriteString("unit", payload.CounterMetadata.CounterUnit);
                writer.WriteString("tags", payload.ValueTags);
                writer.WriteString("counterType", payload.CounterType.ToString());
                if (double.IsFinite(payload.Value))
                {
                    writer.WriteNumber("value", payload.Value);
                }
                else
                {
                    writer.WriteNull("value"); // JSON has no NaN/Infinity
                }
                writer.WriteEndObject();
            }
            _output.WriteByte((byte)'\n');
            _output.Flush();
        }

        public void CounterStopped(CounterPayload payload) { }

        public void Stop() { }
    }
}
