// bug S4 复现配套库（具名导入顶层函数/全局 const 的被导入侧）：
// namespace scene.geom 提供顶层函数 pickAxis（两份重载——导入名字即
// 全部重载入池）、全局 const axisBoost 与类型 Vec2
namespace scene.geom

pub class Vec2 {
    pub const x: i32
    pub const y: i32
    pub init(x: i32, y: i32) {
        this.x = x
        this.y = y
    }
}

pub const axisBoost: i32 = 10

pub func pickAxis(v: Vec2): String {
    if ((v.x * v.x) > (v.y * v.y)) { return "x" }
    return "y"
}

pub func pickAxis(x: i32, y: i32): String {
    if ((x * x) > (y * y)) { return "x" }
    return "y"
}
