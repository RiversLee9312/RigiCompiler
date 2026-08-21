import core.io.Console

@WrapperTarget(.Entity)
pub wrapper Audit {
    pub init()
    operator .proxy.*\<named TNamedArgs..., TUnnamedArgs..., TReturn>(
        symbol: String, namedArgs: named TNamedArgs..., unnamedArgs: TUnnamedArgs...
    ): TReturn {
        Console.println("[Audit] ${symbol}")
        return inner(symbol=symbol, namedArgs=namedArgs, unnamedArgs=unnamedArgs)
    }
    operator .proxy.get.*\<TValue>(symbol: String, value: TValue): TValue {
        Console.println("[Audit] ${symbol}")
        return value
    }
    operator .proxy.set.*\<TValue>(symbol: String, value: TValue) {
        Console.println("[Audit] ${symbol}")
        inner(symbol=symbol, value=value)
    }
}

@Audit()
pub open class Entity {
    pub var name: String = "Ada"
    pub var hp: i32 {
        pub get(value: _) { return value }
        pub set(value: _) { }
    } = 10
    pub init() {
        Console.println("Entity.init ${name} hp=${this.hp}")
    }
}

@Audit()
pub class Hero : Entity {
    pub init() {
        super()
        Console.println("Hero.init")
    }
}

pub func main(): i32 {
    const h = new Hero()
    Console.println("ok ${h.name}")
    return 0
}

