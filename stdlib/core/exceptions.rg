// Rigi 标准库：异常具体子类（S10，SYNTAX §8.1）。
// 异常根 core.Exception 是语言级内建（编译器 bootstrap 携带 protected
// message 字段 + pub abstract getMessage()）；本文件提供五个具体子类，
// 用户自定义异常以同样的 `: core.Exception` 声明。每个子类**自持**显式
// init（Rigi 无 super 构造语法——init 体直接赋值继承字段 message，
// RangeEnumerator 先例；参数名 text 避开字段名防遮蔽）。
// getMessage 按用户裁定为多态抽象：各子类 override 返回自身 message 字段
// （中间层 RuntimeException.getMessage 标 open 供再派生覆写；叶类
// IOException/CastException/NoSuchMethodException/DividedByZeroException
// 直接覆写）。
// 层级：具体子类一律继承 RuntimeException（§8 示例 catch IOException
// 与 catch RuntimeException 并列，说明二者兼容）。
namespace core

pub open class RuntimeException : core.Exception {
    pub init(text: String) { message = text }
    pub open override func getMessage(): String { return message }
}

pub open class IOException : RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

pub open class CastException : RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

pub open class NoSuchMethodException : RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}

pub open class DividedByZeroException : RuntimeException {
    pub init(text: String) { message = text }
    pub override func getMessage(): String { return message }
}
