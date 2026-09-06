// expect-error: is inaccessible
pub func main(): i32 {
    const hidden = new core.serialization.SerializationGraphContext(true)
    return 0
}
