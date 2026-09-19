// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Graphs;

namespace Microsoft.Diagnostics.Tools.GCDump
{
    /// <summary>
    /// Adds '--format' to the convert command, so a nettrace or an existing gcdump can be turned into an alternate
    /// format. Lives in its own file so the upstream handler only needs the 'partial' keyword.
    /// </summary>
    internal static partial class ConvertCommandHandler
    {
        private static readonly Option<GCDumpFileFormat> FormatOption =
            new("--format")
            {
                Description = $"Sets the output format for the conversion. Valid options are: {string.Join(", ", Enum.GetNames<GCDumpFileFormat>())}. The default format is {GCDumpFileFormat.GCDump}.",
                DefaultValueFactory = _ => GCDumpFileFormat.GCDump
            };

        public static Command ConvertCommandWithFormat()
        {
            Command convertCommand = ConvertCommand();
            convertCommand.Description = "Converts nettrace file into .gcdump file handled by analysis tools, or a nettrace/gcdump file into an alternate format.";
            convertCommand.Add(FormatOption);
            convertCommand.SetAction((parseResult, ct) => Task.FromResult(ConvertFile(
                input: parseResult.GetValue(InputPathArgument),
                output: parseResult.GetValue(OutputPathOption) ?? string.Empty,
                verbose: parseResult.GetValue(VerboseOption),
                format: parseResult.GetValue(FormatOption))));
            return convertCommand;
        }

        private static int ConvertFile(FileInfo input, string output, bool verbose, GCDumpFileFormat format)
        {
            if (!Enum.IsDefined(format))
            {
                Console.Error.WriteLine($"Please specify a valid option for the --format. Valid options are: {string.Join(", ", Enum.GetNames<GCDumpFileFormat>())}.");
                return -1;
            }

            if (input == null || !input.Exists)
            {
                Console.Error.WriteLine($"File '{input?.FullName}' does not exist.");
                return -1;
            }

            if (format == GCDumpFileFormat.GCDump)
            {
                // The upstream conversion deletes its output before reading the input, so refuse a gcdump input up front
                if (!GCDumpFileFormatConverter.IsNetTraceFile(input.FullName))
                {
                    Console.Error.WriteLine($"'{input.FullName}' is not a nettrace file. Only nettrace files can be converted to the gcdump format; use '--format Json' to convert a gcdump.");
                    return -1;
                }

                return ConvertFile(input, output, verbose);
            }

            GCHeapDump dump;
            try
            {
                // A nettrace has to be replayed into a graph first; a gcdump already is one
                dump = GCDumpFileFormatConverter.IsNetTraceFile(input.FullName)
                    ? ReadNetTrace(input, verbose)
                    : new GCHeapDump(input.FullName);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to open '{input.FullName}': {ex.GetBaseException().Message}");
                return -1;
            }

            if (dump == null)
            {
                return -1;
            }

            string outputFilename = GCDumpFileFormatConverter.GetConvertedFilename(input.FullName, output, format);
            if (File.Exists(outputFilename))
            {
                File.Delete(outputFilename);
            }

            try
            {
                GCDumpFileFormatConverter.ConvertToFormat(Console.Out, format, dump, outputFilename);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to convert '{input.FullName}' to {format}: {ex.Message}");
                return -1;
            }

            return 0;
        }

        private static GCHeapDump ReadNetTrace(FileInfo input, bool verbose)
        {
            DotNetHeapInfo heapInfo = new();
            TextWriter log = verbose ? Console.Out : TextWriter.Null;
            MemoryGraph memoryGraph = new(50_000);

            if (!EventPipeDotNetHeapDumper.DumpFromEventPipeFile(input.FullName, memoryGraph, log, heapInfo))
            {
                Console.Error.WriteLine($"Failed to convert '{input.FullName}'. The input file may not be a valid nettrace file, or it may not contain GC heap events. Try running with '-v' for more information.");
                return null;
            }

            try
            {
                memoryGraph.AllowReading();
            }
            catch (ApplicationException ex)
            {
                Console.Error.WriteLine($"Failed to convert '{input.FullName}': {ex.Message} The nettrace file may not contain a complete GC heap dump. Try running with '-v' for more information.");
                return null;
            }

            return new GCHeapDump(memoryGraph) { CreationTool = "dotnet-gcdump", DotNetHeapInfo = heapInfo };
        }
    }
}
