using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using static Antlr4.Runtime.Atn.SemanticContext;

namespace Compilator
{
    internal class Program
    {
        static void Main(string[] args)
        {
            string command;
            do {
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

                // Start rule – podle gramatiky je to "program"
                var tree = parser.program();

                Console.WriteLine("Parsování dokončeno bez chyb.");
                Console.WriteLine(tree.ToStringTree(parser));

                var semanticAnalyzer = new SemanticAnalyzer();
                semanticAnalyzer.Visit(tree);
            } while((command = Console.ReadLine()) != "end");
           
        }
    }
}
