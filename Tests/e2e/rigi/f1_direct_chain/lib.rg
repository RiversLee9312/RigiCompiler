// F1/V-B e2e 配套库（负例·多文件同组）：priv Hidden 经 pub 字段直链泄出。
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub class DirectHolder {
    pub init()
    pub var h: Hidden = new Hidden()
}
