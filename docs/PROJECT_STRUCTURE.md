# Latte Compiler 项目结构

**更新日期**: 2026-07-17  
**重构版本**: v1.0

## 目录结构

```
LatteCompiler/
├── AST/                          # 抽象语法树节点定义
│   └── LiteralNodes.cs          # 字面量 AST 节点
│
├── Parser/                       # 语法解析器层
│   ├── Parser.cs                # Parser 主类（状态机驱动）
│   ├── RootParserLayer.cs       # 根解析层
│   ├── LiteralParserLayer.cs    # 字面量解析层（P0 已完成）
│   ├── DeclarationParserLayer.cs # 声明解析层
│   ├── CodeBlockParserLayer.cs  # 代码块解析层
│   ├── ImportParserLayer.cs     # import 语句解析层
│   └── PathParserLayer.cs       # 路径表达式解析层
│
├── Lexer/                        # 词法分析器层
│   ├── Lexer.cs                 # Lexer 主类
│   └── LexerLayers.cs           # 各种 LexerLayer 实现
│       ├── BaseLexerLayer       # 基础词法层
│       ├── WordLexerLayer       # 单词/标识符层
│       ├── StringLexerLayer     # 字符串层
│       ├── NotationLexerLayer   # 符号层
│       ├── CommentLineLexerLayer # 单行注释层
│       └── CommentBlockLexerLayer # 块注释层
│
├── Core/                         # 核心基础设施
│   ├── Utilities.cs             # 工具类（Helper, Keywords, Notations 等）
│   └── FrontendTypesExtension.cs # 前端类型扩展
│
├── Tests/                        # 测试文件
│   └── LiteralParserTests.cs    # 字面量解析器测试
│
├── docs/                         # 文档
│   ├── SYNTAX.md                # Latte 语言语法参考
│   ├── PARSER_ROADMAP.md        # Parser 实现路线图
│   ├── PARSER_IMPLEMENTATION_SUMMARY.md # 实现总结
│   ├── PROGRESS_REPORT.md       # 进度报告
│   └── PROJECT_STRUCTURE.md     # 本文档
│
└── Program.cs                    # 程序入口

bin/                              # 编译输出（不纳入版本控制）
obj/                              # 编译中间文件（不纳入版本控制）
```

## 架构设计原则

### 1. 分层架构

```
Input (Source Code)
      ↓
   Lexer Layer          # 字符流 → Token 流
      ↓
  Parser Layer          # Token 流 → AST
      ↓
    AST Tree            # 抽象语法树
      ↓
 (Future: Semantic Analysis, Code Generation...)
```

### 2. 状态机驱动

- **Lexer**: 栈式状态机，每个 LexerLayer 处理特定类型的字符序列
- **Parser**: 栈式状态机，每个 ParserLayer 处理特定类型的语法结构

### 3. 职责清晰

| 层级 | 职责 | 示例 |
|------|------|------|
| **Lexer** | 简单的字符识别，生成 token | `3.14` → `[Word "3"]`, `[Notation "."]`, `[Word "14"]` |
| **Parser** | 复杂的语义识别，构建 AST | `[Word "3"]`, `[Notation "."]`, `[Word "14"]` → `FloatLiteralASTNode(3.14)` |
| **AST** | 表示语法结构，供后续阶段使用 | `IntLiteralASTNode`, `FloatLiteralASTNode`, ... |

## 命名约定

### 文件命名
- **ParserLayer**: `XxxParserLayer.cs` - 如 `LiteralParserLayer.cs`
- **LexerLayer**: `XxxLexerLayer.cs` - 通常在 `LexerLayers.cs` 中
- **AST 节点**: `XxxASTNode.cs` - 如 `IntLiteralASTNode`
- **测试**: `XxxTests.cs` - 如 `LiteralParserTests.cs`

### 类命名
- **ParserLayer**: 实现 `IParserLayer` 接口
- **LexerLayer**: 实现 `ILexerLayer` 接口
- **AST 节点**: 继承 `ASTNode` 基类

### 命名空间
- 统一使用 `LatteCompiler` 命名空间（暂不使用子命名空间，保持简单）

## 添加新功能指南

### 添加新的 Parser Layer

1. 在 `Parser/` 目录创建 `XxxParserLayer.cs`
2. 实现 `IParserLayer` 接口
3. 在需要的地方 `PushLayer` 你的新 Layer
4. 在 `Tests/` 目录创建对应的测试文件

### 添加新的 AST 节点

1. 在 `AST/` 目录创建或编辑相关的 AST 节点文件
2. 继承 `ASTNode` 基类
3. 添加必要的字段来存储语法信息
4. 在 `ASTNodeType` 枚举中添加新类型（如果需要）

### 添加新的 Lexer Layer

1. 在 `Lexer/LexerLayers.cs` 中添加新的 Layer 类
2. 实现 `ILexerLayer` 接口
3. 在 `BaseLexerLayer` 或其他地方 `PushLayer` 你的新 Layer

## 当前进度

### ✅ 已完成（P0 阶段）
- **LiteralParserLayer**: 完整的字面量解析
  - 整数字面量（所有后缀）
  - 浮点数字面量（状态机处理多 token）
  - 布尔字面量
  - null 字面量
  - 字符串字面量（含插值检测）

### ⏳ 进行中
- 项目结构重构 ✅ 已完成

### 📋 下一步（P0 阶段）
- TypeReferenceParserLayer (P0-2)
- VariableDeclarationParserLayer (P0-3)

## 技术债务

1. **字符字面量未实现** - LiteralParserLayer 中有占位符
2. **命名空间** - 当前所有类在同一命名空间，未来可能需要细分
3. **错误恢复** - 当前错误处理较简单，未实现错误恢复机制
4. **性能优化** - 未进行性能测试和优化

## 注意事项

### Git 忽略
确保 `.gitignore` 包含：
```
bin/
obj/
*.user
*.suo
.vs/
```

### 构建
```bash
dotnet build                    # 编译项目
dotnet run                      # 运行项目
echo "2" | dotnet run          # 运行测试模式
```

### 测试
测试通过 `Program.cs` 的菜单选项 2 运行。未来可能迁移到独立的测试项目。

---

**维护者**: LatteCompiler 开发团队  
**最后更新**: 2026-07-17
