#define PERFORM_CONST_PROPAGATION

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace JoshBe_Compilers
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var text = @"
            {
                int x = READ_INT;
                int y = 10;
                if (x < 2)
                {
                    y = y + 3;
                }
                else
                {
                    y = y - 3;
                }

                if (y > 10)
                {
                    x = x * 2;
                }

                return x + y;
            }";

            Parser parser = new(new MemoryStream(Encoding.UTF8.GetBytes(text)));
            var end_region = parser.Parse();
            end_region.Print(new(), new());
        }
    }

    internal class Parser
    {
        private readonly MemoryStream _stream;
        public Parser(MemoryStream stream)
        {
            _stream = stream;
        }

        private char PeekChar()
        {
            int next = _stream.ReadByte();
            _stream.Position--;
            return (char)next;
        }

        private bool TryRead(
            string tok,
            StringComparison comparison = StringComparison.Ordinal)
        {
            if (TryPeek(tok, comparison))
            {
                _stream.Position += tok.Length;
                return true;
            }
            return false;
        }

        private bool TryPeek(
            string tok,
            StringComparison comparison = StringComparison.Ordinal)
        {
            byte[] buf = new byte[tok.Length];
            int read = _stream.Read(buf, 0, tok.Length);

            if (read != tok.Length)
            {
                _stream.Position -= read;
                return false;
            }

            if (!string.Equals(tok, Encoding.UTF8.GetString(buf), comparison))
            {
                _stream.Position -= read;
                return false;
            }

            if (!char.IsLetterOrDigit(tok.Last()))
            {
                _stream.Position -= read;
                return true;
            }

            char c = PeekChar();
            if (!char.IsLetterOrDigit(c))
            {
                _stream.Position -= read;
                return true;
            }

            _stream.Position -= read;
            return false;
        }

        private void AssertRead(
            string tok,
            StringComparison comparison = StringComparison.Ordinal)
        {
            if (!TryRead(tok, comparison))
            {
                throw new Exception($"Expected token '{tok}' not found.");
            }
        }

        private void ConsumeWhitespace()
        {
            while (char.IsWhiteSpace(PeekChar()))
            {
                _stream.Position++;
            }
        }

        public ControlBlock Parse()
        {
            ParseContext context_enter = new()
            {
                SymbolValues  = new(),
                ScopeStack    = new[] { new SymbolTable(null) },
                CurrentRegion = new()
                {
                    DebugText = "Start",
                    InputRegions = Array.Empty<ControlBlock>(),
                },
                ReturnList = new(),
            };

            ConsumeWhitespace();
            var context_exit = ParseBlock(context_enter);

            var end_region = new ControlBlock()
            {
                DebugText = "End",
                InputRegions = new[] { context_exit.CurrentRegion },
            };
            end_region.SetOutputNodes(context_exit.ReturnList.ToArray());

            context_exit.CurrentRegion.SetOutputRegions(end_region);

            return end_region;
        }

        private ParseContext ParseBlock(ParseContext context)
        {
            var blockContext = context.NewScope();

            AssertRead("{");
            while (true)
            {
                ConsumeWhitespace();
                if (TryRead("}"))
                {
                    ConsumeWhitespace();
                    var endContext = blockContext.PopScope();
                    if (endContext.SymbolTable != context.SymbolTable)
                    {
                        throw new Exception("Symbol table mismatch on scope pop.");
                    }
                    return endContext;
                }

                blockContext = ParseStatement(blockContext);
            }
        }

        public ParseContext ParseStatement(ParseContext context)
        {
            if (TryRead("return"))
            {
                ConsumeWhitespace();
                var exp = ParseExpression(context);
                AssertRead(";");
                ConsumeWhitespace();

                Instruction returnNode = new()
                {
                    OpCode = OpCodes.Return,
                    InputNodes = exp != null ? new[] { exp } : Array.Empty<Instruction>(),
                    InputRegions = new[] { context.CurrentRegion },
                };
                context.ReturnList.Add(returnNode);

                return context;
            }

            if (TryPeek("int"))
            {
                ParseDeclaration(context);
                return context;
            }

            if (TryPeek("if"))
            {
                return ParseIfElse(context);
            }

            long pos = _stream.Position;
            var ident = ParseIdentifier(context);
            var symbol = context.SymbolTable.Resolve(ident);

            if (TryRead("="))
            {
                ConsumeWhitespace();
                var exp = ParseExpression(context);
                exp.DebugSymbol = symbol;
                context.SymbolValues[symbol] = exp;
                AssertRead(";");
                ConsumeWhitespace();

                return context;
            }
            throw new Exception("Unexpected statement.");
        }

        public void ParseDeclaration(ParseContext context)
        {
            AssertRead("int");
            ConsumeWhitespace();
            var ident = ParseIdentifier(context);

            var symbol = context.SymbolTable.Define(ident);
            Instruction? exp = null;
            if (TryRead("="))
            {
                ConsumeWhitespace();
                exp = ParseExpression(context);

                exp.DebugSymbol = symbol;
                context.SymbolValues[symbol] = exp;
            }
            AssertRead(";");
            ConsumeWhitespace();
        }

        public ParseContext ParseIfElse(ParseContext context)
        {
            AssertRead("if");
            ConsumeWhitespace();
            AssertRead("(");
            var condition = ParseExpression(context);
            AssertRead(")");
            ConsumeWhitespace();

#if PERFORM_CONST_PROPAGATION
            if (condition.ConstValue != null)
            {
                if ((bool)condition.ConstValue)
                {
                    var after_block = ParseBlock(context);
                    if (TryRead("else"))
                    {
                        ConsumeWhitespace();
                        ParseBlock(context.NewRegion("const-expr-removed", new[] { context.CurrentRegion }));
                    }
                    return after_block;
                }
                else
                {
                    ParseBlock(context.NewRegion("const-expr-removed", new[] { context.CurrentRegion }));
                    if (TryRead("else"))
                    {
                        ConsumeWhitespace();
                        return ParseBlock(context);
                    }
                    return context;
                }
            }
#endif

            context.CurrentRegion.SetOutputNodes(condition);
            var when_true_enter = context.NewRegion("true", new[] { context.CurrentRegion });
            var when_true_exit = ParseBlock(when_true_enter);

            ParseContext merge;
            ParseContext when_false_enter;
            ParseContext when_false_exit;
            if (TryRead("else"))
            {
                ConsumeWhitespace();

                when_false_enter = context.NewRegion("false", new[] { context.CurrentRegion });
                when_false_exit = ParseBlock(when_false_enter);

                merge = context.NewRegion(new[] { when_true_exit.CurrentRegion, when_false_exit.CurrentRegion });
                when_false_exit.CurrentRegion.SetOutputRegions(merge.CurrentRegion);
            }
            else
            {
                merge = context.NewRegion(new[] { when_true_exit.CurrentRegion, context.CurrentRegion });
                when_false_enter = merge;
                when_false_exit = merge;
            }


            context.CurrentRegion.SetOutputRegions(when_true_enter.CurrentRegion, when_false_enter.CurrentRegion);
            when_true_exit.CurrentRegion.SetOutputRegions(merge.CurrentRegion);

            foreach (var symbol in context.SymbolValues.Keys)// Won't work for setting uninitialized variables
            {
                var phi_common = context.SymbolValues[symbol];
                var phi_true   = when_true_exit.SymbolValues.GetValueOrDefault(symbol, phi_common);
                var phi_false  = when_false_exit.SymbolValues.GetValueOrDefault(symbol, phi_common);

                if (phi_true == phi_false)
                {
                    if (phi_true != phi_common)
                    {
                        merge.SymbolValues[symbol] = phi_true;
                    }
                    continue;
                }

                Instruction phi = new()
                {
                    OpCode       = OpCodes.Phi,
                    DebugSymbol  = symbol,
                    InputNodes   = new[] { phi_true,                     phi_false },
                    InputRegions = new[] { when_true_exit.CurrentRegion, when_false_exit.CurrentRegion },
                };

                merge.SymbolValues[symbol] = phi;
            }

            return merge;
        }

        public string ParseIdentifier(ParseContext context)
        {
            char c = (char)_stream.ReadByte();
            if (!char.IsLetter(c) && c != '_')
            {
                throw new Exception("Expected identifier.");
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(c);
            while (true)
            {
                c = (char)_stream.ReadByte();
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    _stream.Position--;
                    break;
                }
                sb.Append(c);
            }

            ConsumeWhitespace();

            return sb.ToString();
        }

        public Instruction ParseExpression(ParseContext context)
        {
            Instruction left;
            if (TryRead("READ_INT"))
            {
                left = new Instruction()
                {
                    OpCode       = OpCodes.ReadInteger,
                    InputRegions = new[] { context.CurrentRegion },
                    InputNodes   = Array.Empty<Instruction>(),
                };
            }
            else
            {
                char c = PeekChar();
                if (char.IsDigit(c))
                {
                    left = ParseIntegerLiteral(context);
                }
                else
                {
                    var ident = ParseIdentifier(context);
                    var symbol = context.SymbolTable.Resolve(ident);
                    if (!context.SymbolValues.TryGetValue(symbol, out left))
                    {
                        throw new Exception($"Symbol '{ident}' not assigned.");
                    }
                }
            }

            if (!TryParseBinaryExpression(left, context, out var binExp))
            {
                return left;
            }
            return binExp;
        }

        public bool TryParseBinaryExpression(Instruction left, ParseContext context, [NotNullWhen(true)] out Instruction? exp)
        {
            OpCodes op;
            if (TryRead("+"))
            {
                op = OpCodes.Add;
            }
            else if (TryRead("-"))
            {
                op = OpCodes.Subtract;
            }
            else if (TryRead("*"))
            {
                op = OpCodes.Multiply;
            }
            else if (TryRead("<"))
            {
                op = OpCodes.CmpLess;
            }
            else if (TryRead(">"))
            {
                op = OpCodes.CmpGreater;
            }
            else
            {
                exp = null;
                return false;
            }

            ConsumeWhitespace();
            var right = ParseExpression(context);
#if PERFORM_CONST_PROPAGATION
            if (left.ConstValue != null && right.ConstValue != null)
            {
                object constValue;
                switch (op)
                {
                    case OpCodes.Add:
                        constValue = (int)left.ConstValue + (int)right.ConstValue;
                        break;
                    case OpCodes.Subtract:
                        constValue = (int)left.ConstValue - (int)right.ConstValue;
                        break;
                    case OpCodes.Multiply:
                        constValue = (int)left.ConstValue * (int)right.ConstValue;
                        break;
                    case OpCodes.CmpLess:
                        constValue = (int)left.ConstValue < (int)right.ConstValue;
                        break;
                    case OpCodes.CmpGreater:
                        constValue = (int)left.ConstValue > (int)right.ConstValue;
                        break;
                    default:
                        throw new Exception("invalid binexpr op");
                }
                exp = new Instruction()
                {
                    OpCode       = OpCodes.Immediate,
                    InputRegions = new[] { context.CurrentRegion },
                    InputNodes   = Array.Empty<Instruction>(),
                    ConstValue   = constValue,
                };
                return true;
            }
#endif

            // TODO JOSH: Order of Operations
            exp = new Instruction()
            {
                OpCode       = op,
                InputRegions = new[] { context.CurrentRegion },
                InputNodes   = new[] { left, right },
            };
            return true;
        }

        public Instruction ParseIntegerLiteral(ParseContext context)
        {
            char c = (char)_stream.ReadByte();
            if (!char.IsDigit(c))
            {
                throw new Exception("Expected integer literal.");
            }
            int value = c - '0';
            while (true)
            {
                c = (char)_stream.ReadByte();
                if (!char.IsDigit(c))
                {
                    _stream.Position--;
                    break;
                }
                value = value * 10 + (c - '0');
            }
            ConsumeWhitespace();

            return new Instruction()
            {
                OpCode       = OpCodes.Immediate,
                InputRegions = new[] { context.CurrentRegion },
                InputNodes   = Array.Empty<Instruction>(),
                ConstValue        = value,
            };
        }
    }

    internal class ParseContext
    {
        public required Dictionary<Symbol, Instruction> SymbolValues { get; init; }
        public required ControlBlock CurrentRegion { get; init; }
        public required List<Instruction> ReturnList { get; init; }
        public          SymbolTable   SymbolTable => ScopeStack[0];
        public required SymbolTable[] ScopeStack { get; init; }

        public ParseContext NewScope()
        {
            return new ParseContext()
            {
                SymbolValues  = new(SymbolValues),
                ScopeStack    = ScopeStack.Prepend(new(SymbolTable)).ToArray(),
                CurrentRegion = CurrentRegion,
                ReturnList    = ReturnList,
            };
        }

        public ParseContext PopScope()
        {
            if (ScopeStack.Length <= 1)
            {
                throw new Exception("Cannot pop from empty scope stack.");
            }

            return new ParseContext()
            {
                SymbolValues  = new(SymbolValues),
                ScopeStack    = ScopeStack.Skip(1).ToArray(),
                CurrentRegion = CurrentRegion,
                ReturnList    = ReturnList,
            };
        }

        public ParseContext NewRegion(ControlBlock[] inputRegions)
        {
            return NewRegion(null, inputRegions);
        }

        public ParseContext NewRegion(string debugText, ControlBlock[] inputRegions)
        {
            return new ParseContext()
            {
                SymbolValues  = new(SymbolValues),
                ScopeStack    = ScopeStack,
                CurrentRegion = new()
                {
                    InputRegions = inputRegions,
                    DebugText    = debugText,
                },
                ReturnList    = ReturnList,
            };
        }
    }

    internal enum OpCodes
    {
        Immediate,
        Add,
        Subtract,
        Multiply,
        ReadInteger,
        Return,

        CmpLess,
        CmpGreater,

        Phi,
    };

    internal class Instruction // AKA "Node"
    {
        public readonly long ID;

        private static long _id = 0;
        public Instruction()
        {
            ID = Interlocked.Increment(ref _id);
        }

        public required ControlBlock[] InputRegions { get; init; }
        public required Instruction[]  InputNodes  { get; init; }
        public required OpCodes        OpCode { get; init; }

        public Symbol? DebugSymbol { get; set; }

        public object? ConstValue { get; init; }

        public void Print(
            HashSet<ControlBlock> printedRegions,
            HashSet<Instruction>  printedNodes)
        {
            if (!printedNodes.Add(this))
            {
                return;
            }

            Console.WriteLine($"[<node id='n{ID}'>{OpCode}|");
            Console.WriteLine($"    Value: {ConstValue}");
            Console.WriteLine($"    Symbol: {DebugSymbol?.Name}");

            if (OpCode == OpCodes.Phi)
            {
                Console.WriteLine("|");
                for (int i = 0; i < InputRegions.Length; i++)
                {
                    var region = InputRegions[i];
                    Console.WriteLine($"    {i}: Region {region.ID}");
                }
            }
            Console.WriteLine("]");

            if (OpCode == OpCodes.Phi)
            {
                for (int i = 0; i < InputRegions.Length; i++)
                {
                    var region = InputRegions[i];
                    var instr = InputNodes[i];
                    region.Print(printedRegions, printedNodes);
                    instr.Print(printedRegions, printedNodes);
                    Console.WriteLine($"[<node id='n{ID}'>]->{i}[<node id='n{instr.ID}'>]");
                }
            }
            else
            {
                for (int i = 0; i < InputNodes.Length; i++)
                {
                    var instr = InputNodes[i];
                    instr.Print(printedRegions, printedNodes);
                    Console.WriteLine($"[<node id='n{ID}'>]->{i}[<node id='n{instr.ID}'>]");
                }
            }
        }
    }

    internal class ControlBlock // AKA Region
    {
        public readonly long ID;

        private static long _id = 0;
        public ControlBlock()
        {
            ID = Interlocked.Increment(ref _id);
        }

        public string?         DebugText     { get; init; }
        public required ControlBlock[] InputRegions { get; init; }
        public ControlBlock[]? OutputRegions { get; private set; }
        public Instruction[]?  OutputNodes   { get; private set; }

        public void SetOutputRegions(params ControlBlock[] regions)
        {
            if (OutputRegions != null)
            {
                throw new Exception("OutputRegions already set");
            }
            OutputRegions = regions;
        }

        public void SetOutputNodes(params Instruction[] nodes)
        {
            if (OutputNodes != null)
            {
                throw new Exception("OutputNodes already set");
            }
            OutputNodes = nodes;
        }

        public void Print(
            HashSet<ControlBlock> printedRegions,
            HashSet<Instruction>  printedNodes)
        {
            if (!printedRegions.Add(this))
            {
                return;
            }

            Console.Write($"[<region id='r{ID}'>Region {ID}");
            if (DebugText != null)
            {
                Console.Write($" ({DebugText})");
            }
            Console.WriteLine("]");

            for (int i = 0; i < (InputRegions?.Length ?? 0); i++)
            {
                var region = InputRegions![i];
                region.Print(printedRegions, printedNodes);
                Console.WriteLine($"[<region id='r{region.ID}'>]-->[<region id='r{ID}'>]");
            }

            for (int i = 0; i < (OutputNodes?.Length ?? 0); i++)
            {
                var instr = OutputNodes![i];
                instr.Print(printedRegions, printedNodes);
                Console.WriteLine($"[<region id='r{ID}'>]->[<node id='n{instr.ID}'>]");
            }
        }
    }

    internal class BranchResult // AKA "Projection"
    {
        public required ControlBlock[] Regions { get; init; }
        public required Instruction[]  Input   { get; init; }
    }

    internal class Symbol
    {
        public required string Name { get; init; }
    }

    internal class SymbolTable
    {
        public readonly SymbolTable? Parent;
        public readonly Dictionary<string, Symbol> Symbols = new(StringComparer.OrdinalIgnoreCase);

        public SymbolTable(SymbolTable? parent)
        {
            Parent = parent;
        }

        public Symbol Define(string name)
        {
            var symbol = new Symbol()
            {
                Name = name,
            };
            Symbols.Add(name, symbol);
            return symbol;
        }

        public Symbol Resolve(string name)
        {
            if (Symbols.TryGetValue(name, out var symbol))
            {
                return symbol;
            }
            if (Parent == null)
            {
                throw new Exception($"Symbol '{name}' not found.");
            }
            return Parent.Resolve(name);
        }
    }
}
