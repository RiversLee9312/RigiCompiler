// V-B probe p12c 配套库：可空字段经 if? 回退合成 Hidden
class Hidden {
    pub init()
    pub func n(): i32 { return 1 }
}
pub class PairHolder {
    pub init()
    pub var hn: Hidden? = null
    pub var h: Hidden = new Hidden()
}
