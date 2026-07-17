// See https://aka.ms/new-console-template for more information
using LatteCompiler;
using LatteCompiler.Tests;

Console.WriteLine("Latte Compiler - Choose mode:");
Console.WriteLine("1. Parse file");
Console.WriteLine("2. Run Literal tests");
Console.WriteLine("3. Run TypeReference tests");
Console.WriteLine("4. Run VariableDeclaration tests");
Console.WriteLine("5. Run Expression tests");
Console.WriteLine("6. Run Generic parsing tests");
Console.WriteLine("7. Run GenericParameters tests");
Console.WriteLine("8. Run ParameterList tests");
Console.Write("Enter choice (1-8): ");

string? choice = Console.ReadLine();

if (choice == "2")
{
    // 运行字面量测试
    LiteralParserTests.RunAll();
    return;
}
else if (choice == "3")
{
    // 运行类型引用测试
    TypeReferenceParserTests.RunAll();
    return;
}
else if (choice == "4")
{
    // 运行变量声明测试
    VariableDeclarationTests.RunAll();
    return;
}
else if (choice == "5")
{
    // 运行表达式测试
    ExpressionParserTests.RunAll();
    return;
}
else if (choice == "6")
{
    // 运行泛型解析测试
    GenericParsingTests.RunAll();
    return;
}
else if (choice == "7")
{
    // 运行泛型参数列表测试
    GenericParametersTests.RunAll();
    return;
}
else if (choice == "8")
{
    // 运行函数形参列表测试
    ParameterListTests.RunAll();
    return;
}

// 原有的文件解析逻辑
Console.WriteLine("Please type the path of the test script:");
string? scriptPath;
List<Token> tokens;
var lexer = new Lexer();
do
{
    scriptPath = Console.ReadLine();
    if(scriptPath == null)
    {
        Console.WriteLine("Wrong path.Please re-input.");
    }
} while(scriptPath == null);
using(var stream = new FileStream(scriptPath, FileMode.Open, FileAccess.Read))
{
    using(var reader = new StreamReader(stream))
    {
        var task = lexer.Tokenize(reader,scriptPath);
        task.Wait();
        tokens = task.Result;
        Helper.PrintTokenList(tokens);
        var parser = new Parser();
        var astTree = parser.Parse(tokens);
        Helper.PrintASTNode(astTree);
    }
}
