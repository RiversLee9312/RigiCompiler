namespace LatteCompiler
{
    // P4a 降级重写（SEMANTIC_ARCHITECTURE §6.1）：BoundTree → LoweredTree，
    // 树到树重写。M55 起为 visitor 化架构（docs/compiler/semantic/
    // VISITOR_REWRITE.md）：本类只是瘦入口——LowerEnvironment（只读）+
    // LoweringDriver（逐函数体创建 LowerContext，经类别分派器路由到结构
    // visitor）。
    //
    // 落地范围编年史（细节见各 visitor 文件头注释）：
    // S6：恒等重写最小闭环。S7a：覆盖 P3（S5）全部 Bound 节点。
    // S7b：bool 短路 and/or（§11.3 if + 合成局部）、if 表达式（结果局部 +
    //   前置 if + 值块降级）、复合赋值、if 语句恒等。
    // S7c-1：循环降级（条件求值移入 Judge 块写合成 bool 局部、.breakid
    //   合成局部 .bN、循环映射栈）。
    // S7c-2：实例成员恒等降级 + for 脱糖（for-each 协议三方法复用
    //   LoweredLoop，P4b 零新增）。
    // S7d：switch（全值匹配 → LoweredSwitch；含 pattern → selector 物化
    //   .sN + 嵌套 if 链）+ throw 恒等；同批修复 M46 值块编织缺陷
    //   （TransformStatements → continuation 编织）。
    // S7e：cast 恒等、try（ExceptionSlot 合成 + 有名 catch 体头 cast 编织）、
    //   seq 双形态汇合 LoweredSeqBlock；值块编织扩展（seq 透明、
    //   try-finally 部分终止拦截——transformFailed）。
    // S7f：?. / if? 脱糖（receiver 物化 + null 检查 + unwrap/wrap cast）；
    //   解构（物化 pair + 逐字段读取）。
    // S8a：is/supers/with 与 typeOf 恒等降级（BIL §12.3/§12.5 直接对应）。
    //
    // 输入是无错 BoundTree（任一前置 pass 结束时有 Error 即不推进，§8）。
    public static class Lowerer
    {
        public static IReadOnlyList<LoweredFunctionBody> Lower(
            CompilationUnit unit, IReadOnlyList<BoundFunctionBody> bodies)
        {
            return new LoweringDriver(new LowerEnvironment(unit), bodies).Run();
        }
    }
}
