using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Compiler;
using LLVMSharp.Interop;
using static Antlr4.Runtime.Atn.SemanticContext;

namespace Compilator
{
    internal class Program
    {
        static void Main(string[] args)
        {
            string command;
            do{
                Console.WriteLine("Zadej název příkladu (např. example1.txt):");
                string fileName = Console.ReadLine();

                string path = Path.Combine("Examples", fileName);

                if (!File.Exists(path))
                {
                    Console.WriteLine("Soubor neexistuje.");
                    return;
                }

                string inputText = File.ReadAllText(path);

                // ANTLR pipeline
                AntlrInputStream input = new AntlrInputStream(inputText);
                AlgolSubsetLexer lexer = new AlgolSubsetLexer(input);
                CommonTokenStream tokens = new CommonTokenStream(lexer);
                AlgolSubsetParser parser = new AlgolSubsetParser(tokens);

                if (parser.NumberOfSyntaxErrors == 0)
                {
                    var tree = parser.program();

                    // 1. Sémantická kontrola
                    var semanticAnalyzer = new SemanticAnalyzer();
                    semanticAnalyzer.Visit(tree);

                    // 2. Generování kódu
                    Console.WriteLine("Generuji LLVM IR...");
                    var generator = new LLVMGenerator();
                    generator.Visit(tree);

                    var module = generator.GetModule();

                    // Výpis do konzole pro kontrolu
                    module.Dump();

                    // Kontrola validity
                    if (!module.TryVerify(LLVMVerifierFailureAction.LLVMPrintMessageAction, out string error))
                    {
                        Console.WriteLine($"Chyba verifikace modulu: {error}");
                    }
                    else
                    {
                        // Uložení do souboru
                        string outputFile = "output.ll";
                        module.PrintToFile(outputFile);
                        Console.WriteLine($"Soubor '{outputFile}' byl úspěšně vygenerován.");
                        System.Diagnostics.Process.Start("clang", "output.ll -o program.exe");

                    }
                }
            } while((command = Console.ReadLine()) != "end");
        }
    }
}
