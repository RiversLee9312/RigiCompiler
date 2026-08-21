pub class Bag {
    pub var item: i32
    pub operator getAtIndex(index: i32): i32? { return item }
    pub operator setAtIndex(index: i32, element: i32) { item = element }
}
pub func main(): i32 {
    var b = new Bag()
    b[0] += 5
    return 0
}
