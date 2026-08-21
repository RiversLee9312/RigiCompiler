// bug g4：非 rich 泛型 struct 实例化后不得持有 Object（§3.1.1/§10——
// rich/shared 属性属于类型及其布局闭包，泛型实例化后仍必须满足）。
// 期望：编译失败——Wrap\<User> 的字段 v 经实参 User 持有 Object。
// 注：原报为全局无标注 `const w = new Wrap\<User>(...)`——全局字段
// 初始化器与无标注类型推断本就「明确不做」（Binder 头部注释），该
// 填入点不经过绑定；此处用等价的标注形态（P2 填入点）复现同一违规。
pub class User {
    pub const name: String
    pub init(_ -> name)
}
pub struct Wrap\<T> {
    pub var v: T
    pub init(_ -> v)
}
var w: Wrap\<User> = new Wrap\<User>(new User("x"))
func main() { }
