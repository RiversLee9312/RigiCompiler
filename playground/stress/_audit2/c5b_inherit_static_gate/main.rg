// V-Cb（负例）：继承子句未走填入点——SGate\<Local> 的静态字段 slot
// 代入后非共享安全（闸门 1 经实参收口），修复后在此报错
pub class Local {
    pub init()
}
pub class DGate : SGate\<Local> {
    pub init()
}
pub func main(): i32 { return 0 }
