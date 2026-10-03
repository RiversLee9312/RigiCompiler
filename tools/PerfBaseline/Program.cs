using RigiCompiler.PerfBaseline;

try
{
    if (args.Length == 1 && args[0] == "self-test") return await Baseline.SelfTest();
    if (args.Length == 2 && args[0] == "machine-resources") { MachineResources.Write(args[1]); return 0; }
    if (args.Length != 3 || args[0] != "run")
    {
        Console.Error.WriteLine("用法：PerfBaseline run <profile.json> <新的输出目录> | self-test");
        return 2;
    }
    return await Baseline.Run(args[1], args[2]);
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}
