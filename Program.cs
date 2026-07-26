// See https://aka.ms/new-console-template for more information
using LatteCompiler;
using LatteCompiler.Tests;

// 单命令全量测试：dotnet run -- --test-all（CI 入口；任意失败返回非零退出码）
if (args.Length > 0 && args[0] == "--test-all")
{
    Environment.Exit(TestRunner.RunAllSuites());
}

Console.WriteLine("Latte Compiler - Choose mode:");
Console.WriteLine("1. Parse file");
Console.WriteLine("2. Run Literal tests");
Console.WriteLine("3. Run TypeReference tests");
Console.WriteLine("4. Run VariableDeclaration tests");
Console.WriteLine("5. Run Expression tests");
Console.WriteLine("6. Run Generic parsing tests");
Console.WriteLine("7. Run GenericParameters tests");
Console.WriteLine("8. Run ParameterList tests");
Console.WriteLine("9. Run Lambda expression tests");
Console.WriteLine("10. Run if expression tests");
Console.WriteLine("11. Run switch expression tests");
Console.WriteLine("12. Run typeOf expression tests");
Console.WriteLine("13. Run CodeBlock tests");
Console.WriteLine("14. Run Loop tests");
Console.WriteLine("15. Run TryCatchFinally tests");
Console.WriteLine("16. Run SeqBlock tests");
Console.WriteLine("17. Run ThrowStatement tests");
Console.WriteLine("18. Run CoroutineOps tests");
Console.WriteLine("19. Run TypeDeclaration tests");
Console.WriteLine("20. Run PropertyAccessor tests");
Console.WriteLine("21. Run Import tests");
Console.WriteLine("22. Run Namespace tests");
Console.WriteLine("23. Run TokenDisposition tests");
Console.WriteLine("24. Run ASTIntegrityValidator tests");
Console.Write("Enter choice (1-24): ");

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
else if (choice == "9")
{
    // 运行 Lambda 表达式测试
    LambdaExpressionTests.RunAll();
    return;
}
else if (choice == "10")
{
    // 运行 if 表达式测试
    IfExpressionTests.RunAll();
    return;
}
else if (choice == "11")
{
    // 运行 switch 表达式测试
    SwitchExpressionTests.RunAll();
    return;
}
else if (choice == "12")
{
    // 运行 typeOf 表达式测试
    TypeOfExpressionTests.RunAll();
    return;
}
else if (choice == "13")
{
    // 运行代码块测试
    CodeBlockTests.RunAll();
    return;
}
else if (choice == "14")
{
    // 运行循环测试
    LoopTests.RunAll();
    return;
}
else if (choice == "15")
{
    // 运行 try-catch-finally 测试
    TryCatchFinallyTests.RunAll();
    return;
}
else if (choice == "16")
{
    // 运行 seq 块测试
    SeqBlockTests.RunAll();
    return;
}
else if (choice == "17")
{
    // 运行 throw 语句测试
    ThrowStatementTests.RunAll();
    return;
}
else if (choice == "18")
{
    // 运行协程操作测试
    CoroutineOpsTests.RunAll();
    return;
}
else if (choice == "19")
{
    // 运行类型声明测试
    TypeDeclarationTests.RunAll();
    return;
}
else if (choice == "20")
{
    // 运行属性访问器测试
    PropertyAccessorTests.RunAll();
    return;
}
else if (choice == "21")
{
    // 运行 import 测试
    ImportTests.RunAll();
    return;
}
else if (choice == "22")
{
    // 运行 namespace 测试
    NamespaceTests.RunAll();
    return;
}
else if (choice == "23")
{
    // 运行 TokenDisposition 协议测试
    TokenDispositionTests.RunAll();
    return;
}
else if (choice == "24")
{
    // 运行 AST 完整性验证器测试
    ASTIntegrityValidatorTests.RunAll();
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
