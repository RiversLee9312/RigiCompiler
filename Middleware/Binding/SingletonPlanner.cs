using System.Collections.Generic;
using RigiCompiler.Bil;
using RigiCompiler.Middleware.Symbols;

namespace RigiCompiler.Middleware.Binding
{
    /// <summary>
    /// singleton 类型收集（MW10 companion 急切初始化）。
    /// </summary>
    public static class SingletonPlanner
    {
        public static IReadOnlyList<string> Collect(MwSymbolTable symbols)
        {
            var list = new List<string>();
            foreach (var type in symbols.Types)
            {
                if (type.IsExternal)
                {
                    continue;
                }
                if (HasSingleton(type.Declaration.Modifiers))
                {
                    list.Add(type.Canonical);
                }
            }
            return list;
        }

        private static bool HasSingleton(IReadOnlyList<BilModifier> modifiers)
        {
            foreach (var modifier in modifiers)
            {
                if (modifier is BilKeywordModifier keyword
                    && keyword.Keyword == BilKeyword.Singleton)
                {
                    return true;
                }
            }
            return false;
        }

        // 类型是否 singleton（含 companion / ..globals.host / 全局字段
        // cell；VM VmContext.IsSingletonType 同口径——只看声明修饰符）
        public static bool IsSingleton(MwTypeSymbol type) =>
            HasSingleton(type.Declaration.Modifiers);

        // 类型是否声明了任何 init 成员
        public static bool HasInitMember(MwTypeSymbol type)
        {
            foreach (var member in type.Members)
            {
                if (member.HasKeyword(BilKeyword.Init))
                {
                    return true;
                }
            }
            return false;
        }

        // 刀5 合成物命名（Mir 层 NewLowering 的占位引用与
        // SingletonLoweringPass 的合成定名共用——定名确定性，先引后存）。
        // get fn 名带 .static. 中缀（BIL 静态成员命名约定，同
        // Holder$.static.smork() 先例）——无中缀会按实例方法落入虚派发
        // 分类（IsVirtualMember），vtable 无槽
        public static string GetFnCanonicalOf(string typeCanonical) =>
            typeCanonical + "$.static.mw.singleton.get()@" + typeCanonical;

        public static string StateFieldOf(string typeCanonical) =>
            typeCanonical + "#.mw.singleton.state@.i32";

        public static string CacheFieldOf(string typeCanonical) =>
            typeCanonical + "#.mw.singleton.cache@" + typeCanonical;
    }

    /// <summary>
    /// singleton 运行时条目（MW10 刀5）：SingletonLoweringPass 为每个
    /// singleton 类型合成三态取实例 fn（T$.static.mw.singleton.get()@T，
    /// .static. 中缀避开虚派发分类）与两个合成静态槽（state i32：
    /// 0=未构造/1=在途/2=就绪；cache 胖引用 = 唯一实例缓存），Emit 侧
    /// 据此发射静态槽（StaticFieldEmitter 同机制，缓存槽随
    /// rigi_globals_cleanup 释放）并在 rigi_entry 的 ..globals.init
    /// 之前逐个调 get fn（VM InitializeSingletons →
    /// InvokeGlobalInitializers → main 启动序同口径）。
    /// </summary>
    public sealed class SingletonEntry
    {
        public SingletonEntry(string typeCanonical, string getFnCanonical,
            string stateFieldSymbol, string cacheFieldSymbol)
        {
            TypeCanonical = typeCanonical;
            GetFnCanonical = getFnCanonical;
            StateFieldSymbol = stateFieldSymbol;
            CacheFieldSymbol = cacheFieldSymbol;
        }

        // singleton 类型 canonical（同时是 cache 槽的类型段）
        public string TypeCanonical { get; }
        // 合成取实例 fn canonical（T$.static.mw.singleton.get()@T）
        public string GetFnCanonical { get; }
        // 三态槽字段符号（T#.mw.singleton.state@.i32）
        public string StateFieldSymbol { get; }
        // 实例缓存槽字段符号（T#.mw.singleton.cache@T）
        public string CacheFieldSymbol { get; }
    }
}
