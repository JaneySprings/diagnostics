// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.CommandLine;
using System.Threading.Tasks;
using Graphs;

namespace Microsoft.Diagnostics.Tools.GCDump
{
    /// <summary>
    /// Adds '--format' to the collect command. Lives in its own file so the upstream handler only needs the
    /// 'partial' keyword and the call to <see cref="ConvertCollectedDump"/> after the gcdump is written.
    /// </summary>
    internal static partial class CollectCommandHandler
    {
        private static readonly Option<GCDumpFileFormat> FormatOption =
            new("--format")
            {
                Description = $"If not using the default gcdump format, an additional file will be emitted with the specified format under the same output name and with the corresponding format extension. Valid options are: {string.Join(", ", Enum.GetNames<GCDumpFileFormat>())}. The default format is {GCDumpFileFormat.GCDump}.",
                DefaultValueFactory = _ => GCDumpFileFormat.GCDump
            };

        // The format of the collection in progress; a process invocation runs a single collection
        private static GCDumpFileFormat s_format = GCDumpFileFormat.GCDump;

        public static Command CollectCommandWithFormat()
        {
            Command collectCommand = CollectCommand();
            collectCommand.Add(FormatOption);
            collectCommand.SetAction((parseResult, ct) => {
                s_format = parseResult.GetValue(FormatOption);
                if (!Enum.IsDefined(s_format))
                {
                    Console.Error.WriteLine($"Please specify a valid option for the --format. Valid options are: {string.Join(", ", Enum.GetNames<GCDumpFileFormat>())}.");
                    return Task.FromResult(-1);
                }

                return Collect(ct,
                    processId: parseResult.GetValue(ProcessIdOption),
                    output: parseResult.GetValue(OutputPathOption) ?? string.Empty,
                    timeout: parseResult.GetValue(TimeoutOption),
                    verbose: parseResult.GetValue(VerboseOption),
                    name: parseResult.GetValue(NameOption),
                    diagnosticPort: parseResult.GetValue(DiagnosticPortOption) ?? string.Empty,
                    dsrouter: parseResult.GetValue(DsRouterOption) ?? string.Empty);
            });
            return collectCommand;
        }

        /// <summary>
        /// Writes the additional file requested with '--format' next to the gcdump that was just written.
        /// </summary>
        private static void ConvertCollectedDump(MemoryGraph memoryGraph, string gcdumpFileName)
        {
            if (s_format == GCDumpFileFormat.GCDump)
            {
                return;
            }

            GCHeapDump dump = new(memoryGraph) { CreationTool = "dotnet-gcdump" };
            string convertedFileName = GCDumpFileFormatConverter.GetConvertedFilename(gcdumpFileName, outputfile: null, s_format);
            GCDumpFileFormatConverter.ConvertToFormat(Console.Out, s_format, dump, convertedFileName);
        }
    }
}
