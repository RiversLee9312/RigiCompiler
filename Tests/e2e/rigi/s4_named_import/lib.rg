// bug S4 配套库文件（正例·多文件同组）：命名空间顶层函数（同名双
// 重载）与全局 const，供 main.rg 具名导入。
namespace scene.geom
pub class Vec2 {
    pub var x: i32
    pub var y: i32
    pub init(_ -> x, _ -> y)
}
pub const axisBoost: i32 = 10
pub func pickAxis(v: Vec2): String { return "vec" }
pub func pickAxis(x: i32, y: i32): String { return "xy" }
