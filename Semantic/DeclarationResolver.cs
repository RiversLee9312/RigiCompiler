namespace LatteCompiler
{
    // P2 声明解析（SEMANTIC_ARCHITECTURE §2，SEMANTIC_ROADMAP S3）：
    // 在 P1 符号壳上填充类型引用与继承图，并完成全部声明侧合法性检查。
    // 七个子任务（本文件内按依赖序执行，序号对应 ROADMAP；执行序为先 2 后 1——
    // init `_ -> field` 省略类型时沿基类链查字段，继承图须先就绪）：
    //   2. 继承 / implements 图 + 循环继承诊断 + 种类与可继承性检查；
    //      随附构造类型 BaseType 统一回填（BackfillConstructedBaseTypes）；
    //   1. 类型引用解析（字段/参数/返回/基类/接口/约束 Bound/注解名），
    //      含泛型实参递归、T? → Nullable\<T>；失败绑 ErrorTypeSymbol 毒化，
    //      后续用到它的检查一律静默跳过（抑制次生噪音，ARCHITECTURE §8）；
    //   3. 修饰符合法性（SYNTAX §3.1.1 / §9.2 / §10 / §14.9 / §16）；
    //      随附访问器声明侧检查与签名回填（§9.4/§9.4.1：修饰符白名单、
    //      可见性落定、const+set、无体 computed 拒绝）、override 配套检查
    //      （§9.2.1：覆写目标存在且 open/abstract、禁止静默隐藏、abstract
    //      位置与体、具体类待实现成员）、声明侧访问控制（§16：类型引用/
    //      继承/约束命中处的使用点检查，AccessChecker 与 P3 共用）；
    //      随附 native 函数声明检查（§4.6：无体/成员必 static/禁 init/operator/
    //      async/泛型/重载、参数与返回类型基元白名单、@NativeLibrary 必填、
    //      @NativeSymbol 缺省取函数名、内建注解禁挂非 native 声明）；
    //   4. rich/shared 单向传染 + 字段闭包检查（§3.1.1 闭包表七行，递归）；
    //   5. 共享安全闸门：全局/静态字段类型必须共享安全（§3.1.1 闸门 1）；
    //   6. 泛型约束声明侧检查（Target 为泛型参数、with 边界为 wrapper）；
    //   7. ext 成员注册到目标类型；wrapper 适用性（@WrapperTarget 三分类 ×
    //      宿主可内嵌性 × shared 目标矩阵 A–D × interface 实现者传染）。
    //   随附（S11）：enum case 结构级检查与判别值落定（§12：洞独占性、
    //      case 名与显式判别值防御复核、Discriminant 写符号；init 模板
    //      绑定归 P3 声明点）。
    //
    // 名字解析查找序（类型引用/注解名/import/ext 目标共用）：
    //   泛型参数（方法 → 宿主类型链）→ 宿主类型链 NestedTypes →
    //   文件命名空间及父链 → 全局命名空间 → import 列表（具名/通配）→
    //   core 命名空间（隐式可见：i32/String/Object 等裸名由此解析）。
    //
    // 明确不做（归后续里程碑）：重载签名级重复判定、enum case 的 init
    // 模板绑定（S11，P3 声明点）、
    // 无标注字段类型推断（P3，
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
            // 继承解析先于类型引用（M40 原序倒置的根因：init `_ -> field` 省略
            // 类型时沿基类链查字段——链须先就绪；两阶段无反向依赖：
            // InheritanceResolver 的基类子句即时解析，不读字段/参数/返回类型）
            InheritanceResolver.Visit(env);
            // 构造类型 BaseType 统一回填（修复驻留早于定义基类解析的陈旧快照与
            // 未代入快照）：必须在消费基类链的全部后续阶段（OverrideChecker/
            // Contagion/FieldClosure 等）之前
            unit.Symbols.BackfillConstructedBaseTypes();
            TypeReferenceResolver.Visit(env);
            // enum case 结构级检查与判别值落定（S11，§12）：纯 AST 结构级，
            // 不依赖 init 参数类型解析（init 模板绑定归 P3 声明点）
            EnumCaseResolver.Visit(env);
            ModifierChecker.Visit(env);
            AccessorChecker.Visit(env);
            OverrideChecker.Visit(env);
            NativeDeclarationChecker.Visit(env);
            ConversionOperatorChecker.Visit(env);
            ContagionChecker.Visit(env);
            FieldClosureChecker.Visit(env);
            SharedSafetyGateChecker.Visit(env);
            GenericConstraintChecker.Visit(env);
            // async 声明侧闸门（S8f，§4.5）依赖约束边界已解析（GenericConstraintChecker
            // 之后——闸门 5 检查约束界的共享安全），参数/返回类型同已就绪
            AsyncGateChecker.Visit(env);
            WrapperTargetResolver.Visit(env);
            ExtensionRegistrar.Visit(env);
            WrapperApplicationChecker.Visit(env);
            // S11a：proxy 声明侧形状校验（§14.2/§14.3/§14.4 canonical shape 与
            // 类别矩阵；依赖 WrapperTarget 与参数/返回类型已解析）
            ProxyShapeChecker.Visit(env);
            // S11a 后半：`.wrapper.` 隐藏字段合成 + Entity 派发链计算与
            // 特化/原始体符号合成（Freeze 前最后的符号写入）
            ProxyDispatchResolver.Visit(env);
            // P2 结束冻结符号图（ARCHITECTURE §2：P3/P4 只读）
            unit.Symbols.Freeze();
        }
    }
}
