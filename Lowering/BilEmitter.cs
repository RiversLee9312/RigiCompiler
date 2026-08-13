using RigiCompiler.Bil;

namespace RigiCompiler
{
    // P4b 发射（SEMANTIC_ARCHITECTURE §6.2）：LoweredTree → BilModule 的机械
    // 线性化，不再有任何语言级决策。M55 起为 visitor 化架构
    // （M55 visitor 化协议）：本类只是瘦入口——
    // EmitEnvironment（模块级共享状态：Module/资源去重表/诊断）+
    // EmittingDriver（逐函数体创建 EmitContext，经类别分派器路由到结构
    // visitor）。
    //
    // 落地范围编年史（细节见各 visitor 文件头注释）：
    // S6：最小闭环——LocalSymbols 符号图全量声明平铺（内建 bootstrap
    //   符号与 ErrorType 不声明——基元经 BIL 别名投影，不是符号引用；
    //   ExternalSymbols 本阶段恒为空段）+ 字面量 Resources 提取
    //   （§4.2：指令不得内联字面量；键 = (类型投影, 字面量原文) 去重，
    //   名 = R_0/R_1... 按首次出现编号）+ 每 LoweredFunctionBody 一个
    //   fn 定义（.args/.vars/单 entry block；表达式物化为临时变量，
    //   §10.1 操作数只能是变量）。
    // S7a：局部声明/赋值（set.var §13.2、set.field.static §13.4）、
    //   §11 运算指令（BilIntrinsicOp → opcode 单点映射表）、带返回值
    //   invoke（§15.1）、new（§14.1，init 选择归 Middleware，发射不写
    //   init 符号）、§19.1 标量资源全形态（bool/char/f32/f64/null
    //   type(...)）。
    // S7b：LoweredIfStatement → 多 block（§16.2 结构化条件：条件物化到
    //   临时变量 → if $c blk(then) blk(else)，无 else 用 none 操作数；
    //   分支 block 落尾自然返回 §9.4，不补 ret——entrypoint 块维持
    //   既有「void 末尾补 ret / 不得落尾」逻辑）；P4a 合成常量（bool）
    //   与字面量同路进 Resources（同键去重）；block id 函数内唯一递增
    //   （if0-then/if0-else，字符集限 A-Za-z0-9_- §5.1，无点号）。
    // S7c-1：LoweredLoop → loop/loop.rev（§16.3/§16.4：条件即合成局部
    //   引用，操作数序 cond/body/none/judge/breakid，块 id
    //   loop0-body/loop0-judge）+ LoweredLoopControl → break/continue
    //   （§16.5）+ .vars 的 .breakid 条目（§9.3：Type null 的合成局部
    //   投影 .breakid 别名）。
    // S7c-2：实例方法 fn 定义（.args 首条 .return 后插 .this =
    //   OwnerType 投影，§9.2/§7.3——ext 成员同形态）、实例 invoke
    //   （receiver 首实参；接口方法符号引用分派归 Middleware）、
    //   get.field/set.field（§13.3）、this → $.this 零指令、
    //   init/operator 的 §8.4 声明形态（init 修饰符 / operator(名)
    //   修饰符 / ext 修饰符）。
    // S7d：LoweredSwitch → switch 指令（§16.6：操作数序
    //   selector/res(常量表)/[blk(item) 表]/blk(default)/breakid，块 id
    //   switch0-item0/switch0-default；§19.4 switch-table<T> 单行资源，
    //   同（header, 元素序列）去重——case 集相同的 switch 共享一张表；
    //   pattern switch 已在 P4a 降为 if 链，不到这里）+
    //   LoweredThrowStatement → throw（§16.9 单操作数）。
    // S7e：LoweredCastExpression → cast/cast.safe（§12.1/§12.2：
    //   SOURCE RESULT type(TARGET_TYPE)，结果先物化 .t 临时变量）+
    //   LoweredSeqBlock → 独立 block + call blk(seqN)（§16.1/§3.4：
    //   volatile → §9.6 block 修饰符）+ LoweredTryStatement → try 指令
    //   （§16.7 四操作数：blk(tryN-body)/$slot/res(catch-table)/
    //   blk(tryN-finally)|none；§19.5 catch-table 多行资源，元素
    //   type(T) -> blk(tryN-catchI)，空 catch 列表出空表——资源经
    //   resourceKeys 同元素序列去重）。
    // S8a：is/supers/with（§12.3 静态 type.X 与 .indirect 动态三形态）+
    //   typeOf（§12.5 getid.var/getid.type）。
    //
    // 统一风格：表达式求值结果一律先物化到 .t 临时变量，再经 set.var 写入目标。
    // 符号引用一律经 CanonicalSymbolPrinter 投影；BilInstruction.Origin 塞
    // LoweredNode（语句级；load 的 Origin 是字面量 LoweredNode），BIL 模型
    // 对中端零依赖（Origin 保持 object?，§6.3）。
    // 有 P4 Error 时 Emit 仍返回模块（调用方按 §8 门槛不推进写盘）。
    public static class BilEmitter
    {
        public static BilModule Emit(CompilationUnit unit,
            IReadOnlyList<LoweredFunctionBody> bodies, string moduleName)
        {
            return new EmittingDriver(new EmitEnvironment(unit, moduleName), bodies).Run();
        }
    }
}
