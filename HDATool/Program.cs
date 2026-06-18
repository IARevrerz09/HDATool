using System;
using System.IO;

using HDATextTool.IO;

namespace HDATextTool
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("HDATextTool by gdkchan");
            Console.WriteLine("Version 0.3.1");
            Console.ResetColor();
            Console.Write(Environment.NewLine);

            if (args.Length < 3)
            {
                PrintUsage();
                return;
            }
            else
            {
                switch (args[0])
                {
                    case "-xhda": HarvestDataArchive.Unpack(args[1], args[2]); break;
                    case "-chda": HarvestDataArchive.Pack(args[2], args[1]); break;
                    case "-xtxt": File.WriteAllText(args[3], HarvestText.Decode(args[1], args[2])); break;
                    case "-ctxt": HarvestText.Encode(File.ReadAllText(args[1]), args[2], args[3]); break;
                    case "-fixelf": HarvestElf.Fix(args[1], uint.Parse(args[2]), uint.Parse(args[3])); break;
                    default: TextOut.PrintError("Invalid command \"" + args[0] + "\" used!"); return;
                }
            }

            Console.Write(Environment.NewLine);
            TextOut.PrintSuccess("Finished!");
        }

        private static void PrintUsage()
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Usage:");
            Console.ResetColor();
            Console.Write(Environment.NewLine);
            
            Console.WriteLine("tool.exe -xhda file.hda out_folder  Extract HDA data");
            Console.WriteLine("tool.exe -chda in_folder file.hda  Create HDA from folder");
            Console.WriteLine("tool.exe -xtxt text.bin pointers.bin out.txt  Extract text");
            Console.WriteLine("tool.exe -ctxt in.txt text.bin pointers.bin  Create text");
            Console.WriteLine("tool.exe -fixelf SLUS_202.51 lba file_size  Recalculate LBA table");
            Console.Write(Environment.NewLine);
        }
    }
}
