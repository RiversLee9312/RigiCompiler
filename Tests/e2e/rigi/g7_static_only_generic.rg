// W2 负例：仅含静态成员的类型不得声明类型参数（Rigi 无 static class，此为等价形态）。
// expect-error: type 'Util' cannot declare type parameters because it has only static members
pub class Util\<T> {
    pub static func count(): i32 { return 0 }
}
pub func main(): i32 { return 0 }
