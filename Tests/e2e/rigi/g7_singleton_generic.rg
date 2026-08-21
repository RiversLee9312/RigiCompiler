// W2 负例：singleton 不得声明类型参数（单实例无法按实例化携带 typeid）。
// expect-error: singleton type 'S' cannot declare type parameters
pub shared singleton class S\<T> {
    pub var v: i32
}
pub func main(): i32 { return 0 }
