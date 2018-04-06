﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Pc.Backend.ASTExt;
using Microsoft.Pc.TypeChecker;
using Microsoft.Pc.TypeChecker.AST;
using Microsoft.Pc.TypeChecker.AST.Declarations;
using Microsoft.Pc.TypeChecker.AST.Expressions;
using Microsoft.Pc.TypeChecker.AST.Statements;
using Microsoft.Pc.TypeChecker.AST.States;
using Microsoft.Pc.TypeChecker.Types;

namespace Microsoft.Pc.Backend
{
    public class PrtCodeGenerator : ICodeGenerator
    {
        private static readonly Dictionary<Type, string> DeclNameParts = new Dictionary<Type, string>
        {
            {typeof(EnumElem), "ENUMELEM"},
            {typeof(Function), "FUNCTION"},
            {typeof(Implementation), "IMPL"},
            {typeof(Interface), "I"},
            {typeof(Machine), "MACHINE"},
            {typeof(NamedEventSet), "EVENTSET"},
            {typeof(NamedModule), "MODULE"},
            {typeof(PEnum), "ENUM"},
            {typeof(PEvent), "EVENT"},
            {typeof(RefinementTest), "REFINEMENT_TEST"},
            {typeof(SafetyTest), "SAFETY_TEST"},
            {typeof(State), "STATE"},
            {typeof(StateGroup), "STATEGROUP"},
            {typeof(TypeDef), "TYPEDEF"},
            {typeof(Variable), "VAR"}
        };

        public IReadOnlyList<CompiledFile> GenerateCode(ITranslationErrorHandler handler, ICompilerOutput log, string projectName,
                                                        Scope globalScope)
        {
            // Manages job-specific state so that jobs can be parallelized
            var context = new CompilationContext(projectName);

            // Create header and source files.
            var cHeader = new CompiledFile(context.HeaderFileName);
            var cSource = new CompiledFile(context.SourceFileName);

            // Write the header file
            WriteGenericHeader(context, cHeader.Stream);

            foreach (IPDecl decl in globalScope.AllDecls)
            {
                WriteForwardDeclaration(context, cHeader.Stream, decl);
            }

            WriteGenericFooter(context, cHeader.Stream);

            // Write includes and common macros, if any
            WriteSourcePrologue(context, cSource.Stream);

            // Write the machine and function bodies into temporary buffer
            var bodyWriter = new StringWriter();
            foreach (IPDecl decl in globalScope.AllDecls)
            {
                WriteSourceDecl(context, decl, bodyWriter);
            }

            // Write all the type definitions and function implementation prototypes
            context.WriteLine(cSource.Stream, "// Type universe for program:");
            foreach (PLanguageType type in context.UsedTypes)
            {
                WriteTypeDefinition(context, type, cSource.Stream);
            }

            context.WriteLine(cSource.Stream);

            context.WriteLine(cSource.Stream, "// Function implementation prototypes:");
            foreach (Function function in AllMethods(globalScope))
            {
                string functionName = context.Names.GetNameForFunctionImpl(function);
                context.WriteLine(cSource.Stream, $"PRT_VALUE* {functionName}(PRT_MACHINEINST* context, PRT_VALUE*** argRefs);");
            }

            context.WriteLine(cSource.Stream);

            // Append all the function bodies and declaration structs (which have forward declarations in the header)
            cSource.Stream.GetStringBuilder().Append(bodyWriter);

            // All done! Return the compiled files.
            return new List<CompiledFile> {cHeader, cSource};
        }

        private IEnumerable<Function> AllMethods(Scope scope)
        {
            foreach (Function function in scope.Functions)
            {
                yield return function;
            }

            foreach (Machine machine in scope.Machines)
            {
                // includes anonymous functions
                foreach (Function method in machine.Methods)
                {
                    yield return method;
                }
            }
        }

        private void WriteSourceDecl(CompilationContext context, IPDecl decl, TextWriter output)
        {
            string declName = GetPrtNameForDecl(context, decl);
            switch (decl)
            {
                case EnumElem enumElem:
                    return;
                case PEnum pEnum:
                    return;
                case Function function:
                    string functionImplName = context.Names.GetNameForFunctionImpl(function);
                    context.WriteLine(output, $"PRT_FUNDECL {declName} =");
                    context.WriteLine(output, "{");
                    context.WriteLine(output, $"\"{function.Name}\","); // name of function in original program
                    context.WriteLine(output, $"&{functionImplName},"); // pointer to implementation
                    context.WriteLine(output, "NULL"); // payload type for anonymous functions: always NULL.
                    context.WriteLine(output, "};");
                    context.WriteLine(output);
                    context.WriteLine(output, $"PRT_VALUE* {functionImplName}(PRT_MACHINEINST* context, PRT_VALUE*** argRefs)");
                    context.WriteLine(output, "{");
                    WriteFunctionBody(context, function, output);
                    context.WriteLine(output, "}");
                    break;
                case Implementation _:
                    // does not produce a struct definition
                    return;
                case Interface @interface:
                    string ifaceRecvSetName;
                    if (@interface.ReceivableEvents is NamedEventSet)
                    {
                        ifaceRecvSetName = ((NamedEventSet) @interface.ReceivableEvents).Name;
                    }
                    else
                    {
                        var interfaceEventSet = new NamedEventSet(@interface.Name + "_RECV", @interface.SourceLocation);
                        interfaceEventSet.AddEvents(@interface.ReceivableEvents.Events);
                        WriteSourceDecl(context, interfaceEventSet, output);
                        ifaceRecvSetName = GetPrtNameForDecl(context, interfaceEventSet);
                    }
                    
                    context.WriteLine(output, $"PRT_INTERFACEDECL {declName} =");
                    context.WriteLine(output, "{");
                    context.WriteLine(output, $"{context.GetNumberForInterface(@interface)}U,");
                    context.WriteLine(output, $"\"{@interface.Name}\",");
                    context.WriteLine(output, $"&{context.Names.GetNameForType(@interface.PayloadType)},");
                    context.WriteLine(output, $"{ifaceRecvSetName}");
                    context.WriteLine(output, "};");
                    break;
                case Machine machine:
                    foreach (Function machineMethod in machine.Methods)
                    {
                        WriteSourceDecl(context, machineMethod, output);
                    }
                    foreach (var state in machine.States)
                    {
                        WriteSourceDecl(context, state, output);
                    }
                    foreach (var subGroup in machine.Groups)
                    {
                        WriteSourceDecl(context, subGroup, output);
                    }

                    break;
                case NamedEventSet namedEventSet:
                    string innerSetName = context.Names.GetTemporaryName(namedEventSet.Name + "_INNER");
                    var eventDeclNames = namedEventSet.Events.Select(x => "&" + GetPrtNameForDecl(context, x)).ToList();
                    context.WriteLine(output, $"PRT_EVENTDECL* {innerSetName}[] = {{ {string.Join(", ", eventDeclNames)} }};");
                    context.WriteLine(output, $"PRT_EVENTSETDECL {declName} =");
                    context.WriteLine(output, "{");
                    context.WriteLine(output, $"{eventDeclNames.Count}U,");
                    context.WriteLine(output, $"{innerSetName},");
                    context.WriteLine(output, "NULL");
                    context.WriteLine(output, "};");
                    break;
                case NamedModule _:
                    // does not produce a struct definition
                    return;
                case PEvent pEvent when pEvent.IsBuiltIn:
                    return;
                case PEvent pEvent when !pEvent.IsBuiltIn:
                    long eventBound = Math.Min(pEvent.Assert == -1 ? uint.MaxValue : (uint) pEvent.Assert,
                                               pEvent.Assume == -1 ? uint.MaxValue : (uint) pEvent.Assume);

                    context.WriteLine(output, $"PRT_EVENTDECL {declName} = ");
                    context.WriteLine(output, "{");
                    context.WriteLine(output, "{ PRT_VALUE_KIND_EVENT, 0U },");
                    context.WriteLine(output, $"\"{pEvent.Name}\",");
                    context.WriteLine(output, $"{eventBound}U,");
                    context.WriteLine(output, $"&{context.Names.GetNameForType(pEvent.PayloadType)}");
                    context.WriteLine(output, "0U,");
                    context.WriteLine(output, "NULL");
                    context.WriteLine(output, "};");
                    break;
                case RefinementTest _:
                    // does not produce a struct definition
                    return;
                case SafetyTest _:
                    // does not produce a struct definition
                    return;
                case TypeDef typeDef:
                    context.WriteLine(output, $"PRT_TYPE* {declName} = &{context.Names.GetNameForType(typeDef.Type)};");
                    return;
                case Variable variable:
                    return;
                case State state:
                    var stateEntryFunName = state.Entry == null ? "NULL" : $"&{GetPrtNameForDecl(context, state.Entry)}";
                    var stateExitFunName = state.Exit == null ? "NULL" : $"&{GetPrtNameForDecl(context, state.Exit)}";

                    var stateIndex = context.GetNumberForState(state);
                    var stateData = BuildActionSets(context, state);

                    WriteSourceDecl(context, stateData.DefersSet, output);
                    WriteSourceDecl(context, stateData.TransSet, output);
                    WriteSourceDecl(context, stateData.DosSet, output);

                    var transArrName = context.Names.GetTemporaryName("TRANS");
                    context.WriteLine(output, $"PRT_TRANSDECL {transArrName}[] =");
                    context.WriteLine(output, "{");
                    for (var i = 0; i < stateData.Trans.Count; i++)
                    {
                        (PEvent triggerEvent, int destIndex, string transFunRef) = stateData.Trans[i];
                        string triggerName = GetPrtNameForDecl(context, triggerEvent);
                        var comma = i == stateData.Trans.Count - 1 ? "" : ",";
                        context.WriteLine(output, $"{{ {stateIndex}, &{triggerName}, {destIndex}, {transFunRef} }}{comma}");
                    }

                    context.WriteLine(output, "};");
                    context.WriteLine(output);

                    var dosArrName = context.Names.GetTemporaryName("DOS");
                    context.WriteLine(output, $"PRT_DODECL {dosArrName}[] =");
                    context.WriteLine(output, "{");
                    for (var i = 0; i < stateData.Dos.Count; i++)
                    {
                        (PEvent triggerEvent, Function transFun) = stateData.Dos[i];
                        string triggerName = GetPrtNameForDecl(context, triggerEvent);
                        var comma = i == stateData.Trans.Count - 1 ? "" : ",";
                        context.WriteLine(output, $"{{ {stateIndex}, &{triggerName}, &{GetPrtNameForDecl(context, transFun)} }}{comma}");
                    }

                    context.WriteLine(output, "}");
                    context.WriteLine(output);

                    context.WriteLine(output, $"PRT_STATEDECL {declName} = ");
                    context.WriteLine(output, "{");
                    context.WriteLine(output, $"\"{state.QualifiedName}\",");
                    
                    // number of transitions
                    context.WriteLine(output, $"{stateData.Trans.Count}U,");
                    // number of do handlers
                    context.WriteLine(output, $"{stateData.Dos.Count}U,");
                    // defers event set
                    context.WriteLine(output, $"&{GetPrtNameForDecl(context, stateData.DefersSet)},");
                    // transition event set
                    context.WriteLine(output, $"&{GetPrtNameForDecl(context, stateData.TransSet)},");
                    // do trigger set
                    context.WriteLine(output, $"&{GetPrtNameForDecl(context, stateData.DosSet)},");
                    // transitions[]
                    // dos []
                    context.WriteLine(output, $"{stateEntryFunName},");
                    context.WriteLine(output, $"{stateExitFunName},");
                    context.WriteLine(output, "};");
                    break;
                case StateGroup stateGroup:
                    foreach (var state in stateGroup.States)
                    {
                        WriteSourceDecl(context, state, output);
                    }
                    foreach (var subGroup in stateGroup.Groups)
                    {
                        WriteSourceDecl(context, subGroup, output);
                    }
                    break;
            }

            context.WriteLine(output);
        }

        private class StateActionResults
        {
            public NamedEventSet DefersSet { get; }
            public NamedEventSet TransSet { get; }
            public NamedEventSet DosSet { get; }

            public List<(PEvent, Function)> Dos { get; }
            public List<(PEvent, int, string)> Trans { get; }

            public StateActionResults(NamedEventSet defersSet, NamedEventSet transSet, NamedEventSet dosSet, List<(PEvent, Function)> dos, List<(PEvent, int, string)> trans)
            {
                DefersSet = defersSet;
                TransSet = transSet;
                DosSet = dosSet;
                Dos = dos;
                Trans = trans;
            }
        }

        private static StateActionResults BuildActionSets(CompilationContext context, State state)
        {
            var defersSet = new NamedEventSet(state.Name + "_DEFERS", state.SourceLocation);
            var transSet = new NamedEventSet(state.Name + "_TRANS", state.SourceLocation);
            var dosSet = new NamedEventSet(state.Name + "_DOS", state.SourceLocation);

            var dos = new List<(PEvent, Function)>();
            var trans = new List<(PEvent, int, string)>();

            foreach (var eventActionPair in state.AllEventHandlers)
            {
                PEvent pEvent = eventActionPair.Key;
                switch (eventActionPair.Value)
                {
                    case EventDefer _:
                        defersSet.AddEvent(pEvent);
                        break;
                    case EventDoAction eventDoAction:
                        dosSet.AddEvent(pEvent);
                        dos.Add((pEvent, eventDoAction.Target));
                        break;
                    case EventGotoState eventGotoState:
                        transSet.AddEvent(pEvent);
                        var transFunName = eventGotoState.TransitionFunction == null
                                               ? "_P_NO_OP"
                                               : GetPrtNameForDecl(context, eventGotoState.TransitionFunction);
                        trans.Add((pEvent, context.GetNumberForState(eventGotoState.Target), "&" + transFunName));
                        break;
                    case EventIgnore _:
                        dosSet.AddEvent(pEvent);
                        dos.Add((pEvent, null));
                        break;
                    case EventPushState eventPushState:
                        transSet.AddEvent(pEvent);
                        trans.Add((pEvent, context.GetNumberForState(eventPushState.Target), "NULL"));
                        break;
                }
            }

            return new StateActionResults(defersSet,transSet,dosSet,dos,trans);
        }

        private void WriteFunctionBody(CompilationContext context, Function function, TextWriter output)
        {
            for (var i = 0; i < function.Signature.Parameters.Count; i++)
            {
                Variable argument = function.Signature.Parameters[i];
                string varName = GetPrtNameForDecl(context, argument);
                context.WriteLine(output, $"PRT_VALUE** {varName} = argRefs[{i}];");
            }

            foreach (Variable localVariable in function.LocalVariables)
            {
                string varName = GetPrtNameForDecl(context, localVariable);
                string varTypeName = context.Names.GetNameForType(localVariable.Type);
                // TODO: optimize away PrtMkDefaultValue if dataflow shows no usages before assignments.
                context.WriteLine(output, $"PRT_VALUE* {varName} = PrtMkDefaultValue({varTypeName});");
            }

            if (function.Signature.ReturnType.IsSameTypeAs(PrimitiveType.Null))
            {
                context.WriteLine(output, "PRT_VALUE* retval = NULL;");
            }
            else
            {
                string nameForReturnType = context.Names.GetNameForType(function.Signature.ReturnType);
                context.WriteLine(output, $"PRT_VALUE* retval = PrtMkDefaultValue({nameForReturnType});");
            }

            context.WriteLine(output);

            // skip unnecessary nesting level.
            if (function.Body is CompoundStmt body)
            {
                foreach (IPStmt stmt in body.Statements)
                {
                    WriteStmt(context, stmt, output);
                }
            }
            else
            {
                WriteStmt(context, function.Body, output);
            }

            output.WriteLine("p_return:");
            foreach (Variable localVariable in function.LocalVariables)
            {
                string varName = GetPrtNameForDecl(context, localVariable);
                context.WriteLine(output, $"PrtFreeValue({varName}); {varName} = NULL;");
            }

            context.WriteLine(output, "return retval;");
        }

        private void WriteStmt(CompilationContext context, IPStmt stmt, TextWriter output)
        {
            context.WriteLine(output, $"// {stmt.GetType().Name}");
            switch (stmt)
            {
                case AnnounceStmt announceStmt:
                    break;
                case AssertStmt assertStmt:
                    context.Write(output, "PrtAssert(PrtPrimGetBool(");
                    WriteExpr(context, assertStmt.Assertion, output);
                    context.WriteLine(output, $"), \"{assertStmt.Message}\");");
                    break;
                case AssignStmt assignStmt:
                    // Free old value
                    context.Write(output, "PrtFreeValue(");
                    WriteExpr(context, assignStmt.Variable, output);
                    context.WriteLine(output, ");");

                    // Assign new value
                    WriteExpr(context, assignStmt.Variable, output);
                    context.Write(output, " = ");
                    WriteExpr(context, assignStmt.Value, output);
                    context.WriteLine(output, ";");
                    break;
                case CompoundStmt compoundStmt:
                    context.WriteLine(output, "{");
                    foreach (IPStmt pStmt in compoundStmt.Statements)
                    {
                        WriteStmt(context, pStmt, output);
                    }

                    context.WriteLine(output, "}");
                    break;
                case CtorStmt ctorStmt:
                    break;
                case FunCallStmt funCallStmt:
                    break;
                case GotoStmt gotoStmt:
                    context.WriteLine(output, "PrtGoto(context, /* state index? */, ");
                    if (gotoStmt.Payload != null)
                    {
                        context.Write(output, "1, ");
                        WriteExpr(context, gotoStmt.Payload, output);
                    }
                    else
                    {
                        context.Write(output, "0");
                    }
                    context.WriteLine(output, ");");
                    break;
                case IfStmt ifStmt:
                    context.Write(output, "if (PrtPrimGetBool(");
                    WriteExpr(context, ifStmt.Condition, output);
                    context.WriteLine(output, "))");
                    WriteStmt(context, ifStmt.ThenBranch, output);
                    if (ifStmt.ElseBranch != null)
                    {
                        context.WriteLine(output, "else");
                        WriteStmt(context, ifStmt.ElseBranch, output);
                    }

                    break;
                case InsertStmt insertStmt:
                    break;
                case MoveAssignStmt moveAssignStmt:
                    break;
                case NoStmt _:
                    return;
                case PopStmt _:
                    context.WriteLine(output, "PrtPop(context);");
                    context.WriteLine(output, "goto p_return;");
                    break;
                case PrintStmt printStmt:
                    WritePrintStmt(context, output, printStmt);
                    break;
                case RaiseStmt raiseStmt:
                    break;
                case ReceiveStmt receiveStmt:
                    break;
                case RemoveStmt removeStmt:
                    break;
                case ReturnStmt returnStmt:
                    if (returnStmt.ReturnValue != null)
                    {
                        context.WriteLine(output, "PrtFreeValue(retval);");
                        context.Write(output, "retval = ");
                        WriteExpr(context, returnStmt.ReturnValue, output);
                        context.WriteLine(output, ";");
                    }

                    context.WriteLine(output, "goto p_return;");
                    break;
                case SendStmt sendStmt:
                    break;
                case SwapAssignStmt swapAssignStmt:
                    break;
                case WhileStmt whileStmt:
                    context.Write(output, "while (PrtPrimGetBool(");
                    WriteExpr(context, whileStmt.Condition, output);
                    context.WriteLine(output, "))");
                    WriteStmt(context, whileStmt.Body, output);
                    break;
            }

            context.WriteLine(output);
        }

        private void WritePrintStmt(CompilationContext context, TextWriter output, PrintStmt printStmt1)
        {
            // format is {str0, n1, str1, n2, ..., nK, strK}
            var printMessageParts = ParsePrintMessage(printStmt1.Message);

            // Optimize for simple case.
            if (printMessageParts.Length == 1)
            {
                context.Write(output, "PrtPrintf(\"");
                context.Write(output, (string) printMessageParts[0]);
                context.WriteLine(output, "\");");
                return;
            }

            // Otherwise build full parameter pack...
            int k = (printMessageParts.Length - 1) / 2;
            context.Write(output, "PrtFormatPrintf(\"");
            context.Write(output, (string) printMessageParts[0]);
            context.Write(output, "\", ");
            context.Write(output, printStmt1.Args.Count.ToString());
            foreach (IPExpr printArg in printStmt1.Args)
            {
                context.Write(output, ", PRT_FUN_PARAM_CLONE, ");
                WriteExpr(context, printArg, output);
            }

            context.Write(output, ", ");
            context.Write(output, k.ToString());
            for (var i = 0; i < k; i++)
            {
                var n = (int) printMessageParts[1 + 2 * i];
                var s = (string) printMessageParts[1 + 2 * i + 1];
                context.Write(output, ", ");
                context.Write(output, n.ToString());
                context.Write(output, ", \"");
                context.Write(output, s);
                context.Write(output, "\"");
            }

            context.WriteLine(output, ");");
        }

        public static object[] ParsePrintMessage(string message)
        {
            var parts = new List<object>();
            var sb = new StringBuilder();
            for (var i = 0; i < message.Length; i++)
            {
                if (message[i] == '{')
                {
                    if (i + 1 == message.Length)
                    {
                        throw new ArgumentException("unmatched opening brace", nameof(message));
                    }

                    if (message[i + 1] == '{')
                    {
                        i++;
                        sb.Append(message[i]);
                    }
                    else if (char.IsDigit(message[i + 1]))
                    {
                        parts.Add(sb.ToString());
                        sb.Clear();

                        var position = 0;
                        while (++i < message.Length && '0' <= message[i] && message[i] <= '9')
                        {
                            position = 10 * position + (message[i] - '0');
                        }

                        if (i == message.Length || message[i] != '}')
                        {
                            throw new ArgumentException("unmatched opening brace in position expression", nameof(message));
                        }

                        parts.Add(position);
                    }
                    else
                    {
                        throw new ArgumentException("opening brace not followed by digits", nameof(message));
                    }
                }
                else if (message[i] == '}')
                {
                    if (i + 1 == message.Length || message[i + 1] != '}')
                    {
                        throw new ArgumentException("unmatched closing brace", nameof(message));
                    }

                    sb.Append(message[i]);
                    i++;
                }
                else
                {
                    sb.Append(message[i]);
                }
            }

            parts.Add(sb.ToString());
            return parts.ToArray();
        }

        private void WriteExpr(CompilationContext context, IPExpr expr, TextWriter output)
        {
            switch (expr)
            {
                case CloneExpr cloneExpr:
                    context.Write(output, "PrtCloneValue(");
                    WriteExpr(context, cloneExpr.SubExpr, output);
                    context.Write(output, ")");
                    break;
                case BinOpExpr binOpExpr:
                    IPExpr binOpLhs = binOpExpr.Lhs;
                    IPExpr binOpRhs = binOpExpr.Rhs;
                    BinOpType binOpType = binOpExpr.Operation;

                    if (binOpType == BinOpType.Eq || binOpType == BinOpType.Neq)
                    {
                        string negate = binOpType == BinOpType.Eq ? "" : "!";
                        context.Write(output, $"PrtMkBoolValue({negate}PrtIsEqualValue(");
                        WriteExpr(context, binOpLhs, output);
                        context.Write(output, ", ");
                        WriteExpr(context, binOpRhs, output);
                        context.Write(output, "))");
                    }
                    else
                    {
                        var (binOpGetter, binOpBuilder) = GetTypeStructureFuns(binOpLhs.Type);
                        context.Write(output, $"{binOpBuilder}(");

                        context.Write(output, $"{binOpGetter}(");
                        WriteExpr(context, binOpLhs, output);
                        context.Write(output, ")");

                        context.Write(output, $" {BinOpToStr(binOpType)} ");

                        context.Write(output, $"{binOpGetter}(");
                        WriteExpr(context, binOpRhs, output);
                        context.Write(output, ")");

                        context.Write(output, ")");
                    }

                    break;
                case BoolLiteralExpr boolLiteralExpr:
                    context.Write(output, $"PrtMkBoolValue({(boolLiteralExpr.Value ? "PRT_TRUE" : "PRT_FALSE")})");
                    break;
                case CastExpr castExpr:
                    break;
                case CoerceExpr coerceExpr:
                    break;
                case ContainsKeyExpr containsKeyExpr:
                    break;
                case CtorExpr ctorExpr:
                    break;
                case DefaultExpr defaultExpr:
                    string nameForDefaultType = context.Names.GetNameForType(defaultExpr.Type);
                    context.Write(output, $"PrtMkDefaultValue({nameForDefaultType})");
                    break;
                case EnumElemRefExpr enumElemRefExpr:
                    context.Write(output, GetPrtNameForDecl(context, enumElemRefExpr.EnumElem));
                    break;
                case EventRefExpr eventRefExpr:
                    break;
                case FairNondetExpr fairNondetExpr:
                    break;
                case FloatLiteralExpr floatLiteralExpr:
                    break;
                case FunCallExpr funCallExpr:
                    break;
                case IntLiteralExpr intLiteralExpr:
                    context.Write(output, $"PrtMkIntValue({intLiteralExpr.Value})");
                    break;
                case KeysExpr keysExpr:
                    break;
                case LinearAccessRefExpr linearAccessRefExpr:
                    break;
                case MapAccessExpr mapAccessExpr:
                    break;
                case NamedTupleAccessExpr namedTupleAccessExpr:
                    break;
                case NamedTupleExpr namedTupleExpr:
                    break;
                case NondetExpr _:
                    context.Write(output, "PrtMkNondetBoolValue()");
                    break;
                case NullLiteralExpr nullLiteralExpr:
                    context.Write(output, "PrtMkNullValue()");
                    break;
                case SeqAccessExpr seqAccessExpr:
                    break;
                case SizeofExpr sizeofExpr:
                    break;
                case ThisRefExpr thisRefExpr:
                    break;
                case TupleAccessExpr tupleAccessExpr:
                    break;
                case UnaryOpExpr unaryOpExpr:
                    var (unOpGetter, unOpBuilder) = GetTypeStructureFuns(unaryOpExpr.Type);
                    context.Write(output, $"{unOpBuilder}(");

                    context.Write(output, UnOpToStr(unaryOpExpr.Operation));
                    context.Write(output, $"{unOpGetter}(");
                    WriteExpr(context, unaryOpExpr.SubExpr, output);
                    context.Write(output, ")");

                    context.Write(output, ")");
                    break;
                case UnnamedTupleExpr unnamedTupleExpr:
                    break;
                case ValuesExpr valuesExpr:
                    break;
                case VariableAccessExpr variableAccessExpr:
                    if (variableAccessExpr.Variable.Role.HasFlag(VariableRole.Param))
                    {
                        // dereference, since params are passed by reference.
                        context.Write(output, "*");
                    }

                    if (variableAccessExpr.Variable.Role.HasFlag(VariableRole.Field))
                    {
                        context.Write(output, "p_this->");
                    }

                    context.Write(output, GetPrtNameForDecl(context, variableAccessExpr.Variable));
                    break;
            }
        }

        private string UnOpToStr(UnaryOpType operation)
        {
            switch (operation)
            {
                case UnaryOpType.Negate:
                    return "-";
                case UnaryOpType.Not:
                    return "!";
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        private static (string, string) GetTypeStructureFuns(PLanguageType type)
        {
            string binOpGetter;
            string binOpBuilder;
            if (type.IsSameTypeAs(PrimitiveType.Int) || type.TypeKind.Equals(TypeKind.Enum))
            {
                binOpGetter = "PrtPrimGetInt";
                binOpBuilder = "PrtMkIntValue";
            }
            else if (type.IsSameTypeAs(PrimitiveType.Bool))
            {
                binOpGetter = "PrtPrimGetBool";
                binOpBuilder = "PrtMkBoolValue";
            }
            else if (type.IsSameTypeAs(PrimitiveType.Float))
            {
                binOpGetter = "PrtPrimGetFloat";
                binOpBuilder = "PrtMkFloatValue";
            }
            else
            {
                throw new ArgumentException($"cannot destructure type {type.CanonicalRepresentation}", nameof(type));
            }

            return (binOpGetter, binOpBuilder);
        }

        private string BinOpToStr(BinOpType binOpType)
        {
            switch (binOpType)
            {
                case BinOpType.Add:
                    return "+";
                case BinOpType.Sub:
                    return "-";
                case BinOpType.Mul:
                    return "*";
                case BinOpType.Div:
                    return "/";
                case BinOpType.Eq:
                    return "==";
                case BinOpType.Neq:
                    return "!=";
                case BinOpType.Lt:
                    return "<";
                case BinOpType.Le:
                    return "<=";
                case BinOpType.Gt:
                    return ">";
                case BinOpType.Ge:
                    return ">=";
                case BinOpType.And:
                    return "&&";
                case BinOpType.Or:
                    return "||";
                default:
                    throw new ArgumentOutOfRangeException(nameof(binOpType), binOpType, null);
            }
        }

        private string WriteTypeDefinition(CompilationContext context, PLanguageType type, TextWriter output)
        {
            type = type.Canonicalize();

            string typeGenName = context.Names.GetNameForType(type);

            if (context.WrittenTypes.Contains(type))
            {
                return typeGenName;
            }

            switch (type)
            {
                case BoundedType boundedType:
                    context.WriteLine(output, $"// TODO: implement types like {boundedType.CanonicalRepresentation}");
                    break;
                case EnumType _:
                    context.WriteLine(output, $"static PRT_TYPE {typeGenName} = {{ PRT_KIND_INT, {{ NULL }} }};");
                    break;
                case ForeignType foreignType:
                    context.WriteLine(output, $"// TODO: implement types like {foreignType.CanonicalRepresentation}");
                    break;
                case MapType mapType:
                    string mapKeyTypeName = WriteTypeDefinition(context, mapType.KeyType, output);
                    string mapValueTypeName = WriteTypeDefinition(context, mapType.ValueType, output);
                    string mapTypeDeclName = context.Names.GetTemporaryName("MAPTYPE");
                    context.WriteLine(output, $"static PRT_MAPTYPE {mapTypeDeclName} = {{ &{mapKeyTypeName}, &{mapValueTypeName} }};");
                    context.WriteLine(output, $"static PRT_TYPE {typeGenName} = {{ PRT_KIND_MAP, {{ &{mapTypeDeclName} }} }};");
                    break;
                case NamedTupleType namedTupleType:
                    string ntNamesArrayName = context.Names.GetTemporaryName("NMDTUP_N");
                    string ntTypesArrayName = context.Names.GetTemporaryName("NMDTUP_T");
                    string ntStructName = context.Names.GetTemporaryName("NMDTUP");
                    var typeDeclNames = namedTupleType.Types.Select(t => WriteTypeDefinition(context, t, output));
                    context.WriteLine(
                        output,
                        $"static PRT_STRING {ntNamesArrayName}[] = {{ {string.Join(", ", namedTupleType.Names.Select(name => "\"" + name + "\""))} }};");
                    context.WriteLine(
                        output,
                        $"static PRT_TYPE* {ntTypesArrayName}[] = {{ {string.Join(", ", typeDeclNames.Select(name => "&" + name))} }};");
                    context.WriteLine(
                        output,
                        $"static PRT_NMDTUPTYPE {ntStructName} = {{ {namedTupleType.Types.Count}U, {ntNamesArrayName}, {ntTypesArrayName} }};");
                    context.WriteLine(output, $"static PRT_TYPE {typeGenName} = {{ PRT_KIND_NMDTUP, {{ &{ntStructName} }} }};");
                    break;
                case PermissionType permissionType:
                    context.WriteLine(output, $"// TODO: implement types like {permissionType.CanonicalRepresentation}");
                    break;
                case PrimitiveType primitiveType when Equals(primitiveType, PrimitiveType.Null):
                    context.WriteLine(output, $"static PRT_TYPE {typeGenName} = {{ PRT_KIND_NULL, {{ NULL }} }};");
                    break;
                case PrimitiveType primitiveType when Equals(primitiveType, PrimitiveType.Int):
                    context.WriteLine(output, $"static PRT_TYPE {typeGenName} = {{ PRT_KIND_INT, {{ NULL }} }};");
                    break;
                case PrimitiveType primitiveType when Equals(primitiveType, PrimitiveType.Float):
                    context.WriteLine(output, $"static PRT_TYPE {typeGenName} = {{ PRT_KIND_FLOAT, {{ NULL }} }};");
                    break;
                case PrimitiveType primitiveType:
                    context.WriteLine(output, $"// TODO: implement types like {primitiveType.CanonicalRepresentation}");
                    break;
                case SequenceType sequenceType:
                    string seqElementTypeName = WriteTypeDefinition(context, sequenceType.ElementType, output);
                    string seqTypeDeclName = context.Names.GetTemporaryName("SEQTYPE");
                    context.WriteLine(output, $"static PRT_SEQTYPE {seqTypeDeclName} = {{ &{seqElementTypeName} }};");
                    context.WriteLine(output, $"static PRT_TYPE {typeGenName} = {{ PRT_KIND_SEQ, {{ &{seqTypeDeclName} }} }};");
                    break;
                case TupleType tupleType:
                    string tupTypesArrayName = context.Names.GetTemporaryName("TUP_T");
                    string tupStructName = context.Names.GetTemporaryName("TUP");
                    var tupTypeDeclNames = tupleType.Types.Select(t => WriteTypeDefinition(context, t, output));
                    context.WriteLine(output, $"static PRT_TYPE* {tupTypesArrayName}[] = {{ {string.Join(", ", tupTypeDeclNames.Select(n => "&" + n))} }};");
                    context.WriteLine(output, $"static PRT_TUPTYPE {tupStructName} = {{ {tupleType.Types.Count}U, {tupTypesArrayName} }};");
                    context.WriteLine(output, $"static PRT_TYPE {typeGenName} = {{ PRT_KIND_TUPLE, {{ &{tupStructName} }} }};");
                    break;
                case TypeDefType _:
                    throw new ArgumentException("typedefs shouldn't be possible after canonicalization", nameof(type));
            }

            context.WrittenTypes.Add(type);
            return typeGenName;
        }

        private void WriteSourcePrologue(CompilationContext context, TextWriter output)
        {
            context.WriteLine(output, $"#include \"{context.HeaderFileName}\"");
            context.WriteLine(output);
        }

        private static string GetPrtNameForDecl(CompilationContext context, IPDecl decl)
        {
            if (decl == null)
            {
                throw new ArgumentNullException(nameof(decl));
            }

            var computedPrefix = "";
            switch (decl)
            {
                case EnumElem enumElem:
                    computedPrefix = $"{enumElem.ParentEnum.Name}_";
                    break;
                case PEvent pEvent:
                    if (pEvent.IsNullEvent)
                    {
                        return "<<prt:null-event>>";
                    }

                    if (pEvent.IsHaltEvent)
                    {
                        return "<<prt:halt-event>>";
                    }

                    break;
            }

            if (DeclNameParts.TryGetValue(decl.GetType(), out string prefix))
            {
                prefix += "_";
            }
            else
            {
                prefix = "";
            }

            return context.Names.GetNameForNode(decl, prefix + computedPrefix);
        }

        private class CompilationContext
        {
            private bool lineHasBeenIndented;
            private readonly Dictionary<Interface, int> interfaceNumbering = new Dictionary<Interface, int>();
            private readonly Dictionary<ValueTuple<Machine, State>, int> stateNumbering = new Dictionary<ValueTuple<Machine, State>, int>();

            public CompilationContext(string projectName)
            {
                ProjectName = projectName;
                HeaderFileName = $"{projectName}.h";
                SourceFileName = $"{projectName}.c";
                Names = new NameManager($"P_{projectName.ToUpperInvariant()}_");
            }

            public string ProjectName { get; }
            public string HeaderFileName { get; }
            public string SourceFileName { get; }
            public NameManager Names { get; }
            public IEnumerable<PLanguageType> UsedTypes => Names.UsedTypes;
            public HashSet<PLanguageType> WrittenTypes { get; } = new HashSet<PLanguageType>();
            public int IndentationLevel { get; set; }

            public int GetNumberForInterface(Interface pInterface)
            {
                if (interfaceNumbering.TryGetValue(pInterface, out int name))
                {
                    return name;
                }

                name = interfaceNumbering.Count;
                interfaceNumbering.Add(pInterface, name);
                return name;
            }

            public int GetNumberForState(State state)
            {
                var machine = state.OwningMachine;
                if (stateNumbering.TryGetValue((machine, state), out int name))
                {
                    return name;
                }

                name = stateNumbering.Count;
                stateNumbering.Add((machine, state), name);
                return name;
            }

            public void WriteLine(TextWriter output, string format = "")
            {
                // Unindent for every } at the beginning of the line, save the index 
                // of one past the last leading }.
                int i;
                for (i = 0; i < format.Length; i++)
                {
                    if (format[i] == '}')
                    {
                        IndentationLevel--;
                    }
                    else if (!char.IsWhiteSpace(format[i]))
                    {
                        break;
                    }
                }

                // Do not indent preprocessor lines.
                var indentation = new string(' ', 4 * IndentationLevel);
                if (format.StartsWith("#") || lineHasBeenIndented)
                {
                    indentation = "";
                }

                output.WriteLine(indentation + format);
                lineHasBeenIndented = false;

                // Compute indentation for future lines starting from after last leading }.
                for (; i < format.Length; i++)
                {
                    if (format[i] == '{')
                    {
                        IndentationLevel++;
                    }
                    else if (format[i] == '}')
                    {
                        IndentationLevel--;
                    }
                }
            }

            public void Write(TextWriter output, string format)
            {
                // Unindent for every } at the beginning of the line, save the index 
                // of one past the last leading }.
                int i;
                for (i = 0; i < format.Length; i++)
                {
                    if (format[i] == '}')
                    {
                        IndentationLevel--;
                    }
                    else if (!char.IsWhiteSpace(format[i]))
                    {
                        break;
                    }
                }

                // Do not indent preprocessor lines.
                var indentation = new string(' ', 4 * IndentationLevel);
                if (format.StartsWith("#") || lineHasBeenIndented)
                {
                    indentation = "";
                }

                output.Write(indentation + format);
                lineHasBeenIndented = true;

                // Compute indentation for future lines starting from after last leading }.
                for (; i < format.Length; i++)
                {
                    if (format[i] == '{')
                    {
                        IndentationLevel++;
                    }
                    else if (format[i] == '}')
                    {
                        IndentationLevel--;
                    }
                }
            }
        }

        #region Header writing routines

        private static void WriteGenericHeader(CompilationContext context, TextWriter output)
        {
            string includeGuardMacro = $"P_{Regex.Replace(context.ProjectName.ToUpperInvariant(), @"\s+", "")}_H_";
            context.WriteLine(output, "#pragma once");
            context.WriteLine(output, $"#ifndef {includeGuardMacro}");
            context.WriteLine(output, $"#define {includeGuardMacro}");
            context.WriteLine(output, "#include \"Prt.h\"");
            context.WriteLine(output);
            context.WriteLine(output, "#ifdef __cplusplus");
            context.WriteLine(output, "extern \"C\" {");
            context.WriteLine(output, "#endif");
            context.WriteLine(output);
        }

        private static void WriteForwardDeclaration(CompilationContext context, TextWriter output, IPDecl decl)
        {
            string declName = GetPrtNameForDecl(context, decl);
            switch (decl)
            {
                case EnumElem enumElem:
                    // TODO: do we write enum elements here?
                    break;
                case Function _:
                    context.WriteLine(output, $"extern PRT_FUNDECL {declName};");
                    break;
                case Implementation implementation:
                    break;
                case Interface _:
                    context.WriteLine(output, $"extern PRT_UINT32 {declName};");
                    break;
                case Machine _:
                    context.WriteLine(output, $"extern PRT_MACHINEDECL {declName};");
                    break;
                case NamedEventSet namedEventSet:
                    context.WriteLine(output, $"// DECL(NamedEventSet, {decl.Name}) => {declName}");
                    break;
                case NamedModule namedModule:
                    context.WriteLine(output, $"// DECL(NamedModule, {decl.Name}) => {declName}");
                    break;
                case PEnum pEnum:
                    context.WriteLine(
                        output,
                        $"typedef enum {declName} {{ {string.Join(", ", pEnum.Values.Select(val => $"{GetPrtNameForDecl(context, val)} = {val.Value}"))} }} {declName};");
                    context.WriteLine(output);
                    break;
                case PEvent pEvent:
                    if (!pEvent.IsBuiltIn)
                    {
                        context.WriteLine(output, $"extern PRT_EVENTDECL {declName};");
                    }

                    break;
                case RefinementTest refinementTest:
                    context.WriteLine(output, $"// DECL(RefinementTest, {decl.Name}) => {declName}");
                    break;
                case SafetyTest safetyTest:
                    context.WriteLine(output, $"// DECL(SafetyTest, {decl.Name}) => {declName}");
                    break;
                case TypeDef typeDef:
                    context.WriteLine(output, $"extern PRT_TYPE* {declName};");
                    break;
                case Variable variable:
                    throw new ArgumentException("can't have global P variables", nameof(decl));
                case State state:
                    context.WriteLine(output, $"// DECL(State, {decl.Name}) => {declName}");
                    break;
                case StateGroup stateGroup:
                    context.WriteLine(output, $"// DECL(StateGroup, {decl.Name}) => {declName}");
                    break;
            }
        }

        private static void WriteGenericFooter(CompilationContext context, TextWriter output)
        {
            string includeGuardMacro = $"P_{Regex.Replace(context.ProjectName.ToUpperInvariant(), @"\s+", "")}_H_";
            context.WriteLine(output);
            context.WriteLine(output, "#ifdef __cplusplus");
            context.WriteLine(output, "}");
            context.WriteLine(output, "#endif");
            context.WriteLine(output);
            context.WriteLine(output, $"#endif // {includeGuardMacro}");
        }

        #endregion
    }
}
