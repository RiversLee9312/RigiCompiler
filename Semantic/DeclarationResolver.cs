namespace LatteCompiler
{
    // P2 声明解析（SEMANTIC_ARCHITECTURE §2，SEMANTIC_ROADMAP S3）：
    // 在 P1 符号壳上填充类型引用与继承图，并完成全部声明侧合法性检查。
    // 七个子任务（本文件内按依赖序执行，序号对应 ROADMAP）：
    //   1. 类型引用解析（字段/参数/返回/基类/接口/约束 Bound/注解名），
    //      含泛型实参递归、T? → Nullable\<T>；失败绑 ErrorTypeSymbol 毒化，
    //      后续用到它的检查一律静默跳过（抑制次生噪音，ARCHITECTURE §8）；
    //   2. 继承 / implements 图 + 循环继承诊断 + 种类与可继承性检查；
    //   3. 修饰符合法性（SYNTAX §3.1.1 / §9.2 / §10 / §14.9 / §16）；
    //      随附 native 函数声明检查（§4.6：无体/成员必 static/禁 init/operator/
    //      async/泛型/重载、参数与返回类型基元白名单、@NativeLibrary 必填、
    //      @NativeSymbol 缺省取函数名、内建注解禁挂非 native 声明）；
    //   4. rich/shared 单向传染 + 字段闭包检查（§3.1.1 闭包表七行，递归）；
    //   5. 共享安全闸门：全局/静态字段类型必须共享安全（§3.1.1 闸门 1）；
    //   6. 泛型约束声明侧检查（Target 为泛型参数、with 边界为 wrapper）；
    //   7. ext 成员注册到目标类型；wrapper 适用性（@WrapperTarget 三分类 ×
    //      宿主可内嵌性 × shared 目标矩阵 A–D × interface 实现者传染）。
    //
    // 名字解析查找序（类型引用/注解名/import/ext 目标共用）：
    //   泛型参数（方法 → 宿主类型链）→ 宿主类型链 NestedTypes →
    //   文件命名空间及父链 → 全局命名空间 → import 列表（具名/通配）→
    //   core 命名空间（隐式可见：i32/String/Object 等裸名由此解析）。
    //
    // 明确不做（归后续里程碑）：访问控制使用点检查、重载签名级重复判定、
    // getter/setter 符号与 enum case（S8/S11）、无标注字段类型推断（P3，
    // 其闭包/闸门检查随推断结果在 P3 复核——见 PROGRESS_REPORT 技术债）。
    // P2 结束冻结符号图（SymbolGraph.Freeze）。
    //
    // 瘦入口（M55 同款 visitor 化重构）：AST 骨架遍历与条目收集归
    // Resolution/EntryCollector（产出只读 ResolveEnvironment），各子任务归
    // Resolution/ 下的阶段 visitor（ResolverVisitor<TSelf> 协议，静态 Visit
    // 唯一入口）。执行顺序与旧 ResolveSession.Run() 逐字一致，行为不变。
    public static class DeclarationResolver
    {
        public static void Resolve(CompilationUnit unit, DeclarationCollection declarations)
        {
            var env = EntryCollector.Collect(unit, declarations);
            ImportValidator.Visit(env);
            TypeReferenceResolver.Visit(env);
            InheritanceResolver.Visit(env);
            ModifierChecker.Visit(env);
            NativeDeclarationChecker.Visit(env);
            ContagionChecker.Visit(env);
            FieldClosureChecker.Visit(env);
            SharedSafetyGateChecker.Visit(env);
            GenericConstraintChecker.Visit(env);
            WrapperTargetResolver.Visit(env);
            ExtensionRegistrar.Visit(env);
            WrapperApplicationChecker.Visit(env);
            // P2 结束冻结符号图（ARCHITECTURE §2：P3/P4 只读）
            unit.Symbols.Freeze();
        }
    }
}
