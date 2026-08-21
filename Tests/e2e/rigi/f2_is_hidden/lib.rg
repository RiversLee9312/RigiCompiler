// F2/V-D 配套库：文件级私有类型 Hidden 经 Object 返回出厂
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub func make(): Object { return new Hidden() }
