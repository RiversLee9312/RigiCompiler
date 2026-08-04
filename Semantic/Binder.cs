namespace LatteCompiler
{
    // P3 函数体分析（SEMANTIC_ARCHITECTURE §5，SEMANTIC_ROADMAP S5 最小闭环）：
    // 以函数体为独立分析单位（函数间诊断互不阻断），AST 只读，产出 BoundTree。
    //
    // M55 起为 visitor 化架构（docs/compiler/semantic/VISITOR_REWRITE.md）：
    // 本类只是瘦入口——BindEnvironment（只读共享）+ BindingDriver（逐函数体
    // 创建 BindContext，经类别分派器路由到结构 visitor）。协议：CRTP 基类
    // BinderVisitor（静态 Visit 入口 + Enter/Exit 生命周期配对）+ 双协议
    // （Visit → TResult? 上行合成 / VisitInto 壳填充）。
    //
    // 落地范围编年史（细节见各 visitor 文件头注释）：
    // S5：字面量定型、局部变量声明与引用（var 推断）、参数引用、全局字段引用、
    //   二元/一元 intrinsic 运算、无重载直接调用（规范参数序）、new、return
    //   全路径检查、definite assignment 最小版。
    // S7b：if 语句/表达式（值块：隐式取值/return@标签）、DA 分支合并、复合赋值。
    // S7c-1：while/do-while（循环标签栈、DA 循环两规则、值块穿透）。
    // S7c-2：this/实例成员链上色/裸名实例成员补 this/for 双形态。
    // S7d：switch 语句/表达式（占位 `_` 栈、值匹配/pattern 分类）、throw。
    // S7e：cast（as/as?）、try-catch-finally、seq 双形态。
    // S7f：字符串插值（绑定即规范化）、?. 安全调用、if? 空值回退、解构声明。
    // S8a：is/supers/with（右侧双形态）与 typeOf（值/类型双形态）。
    // S8d：重载解析（OverloadResolution——结构过滤/类型适用性/最具体胜出 +
    //   平局打破）+ 默认参数（声明点绑定 + 调用点规范序填充）+ 具名参数
    //   重排纳入 ranking；init 与索引读同一设施。
    //
    // 明确不做（归后续里程碑，遇之一律 P3 诊断而非崩溃）：
    // yield、可变参数调用绑定、写模式索引重载（RHS 类型赋值侧才可知）、
    // getter/setter（S8e）、访问控制（S8e）、泛型使用侧（S9）、
    // enum case（S11）、await/lambda（S13）、
    // 全局字段初始化器与无标注字段类型推断。
    public static class Binder
    {
        public static IReadOnlyList<BoundFunctionBody> Bind(CompilationUnit unit,
            DeclarationCollection declarations)
        {
            return new BindingDriver(new BindEnvironment(unit, declarations)).Run();
        }
    }
}
