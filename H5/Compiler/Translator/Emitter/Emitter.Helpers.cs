using H5.Contract;
using H5.Contract.Constants;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.Semantics;
using ICSharpCode.NRefactory.TypeSystem;
using ICSharpCode.NRefactory.TypeSystem.Implementation;
using Mono.Cecil;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace H5.Translator
{
    public sealed partial class Emitter
    {
        protected HashSet<string> CreateNamespaces()
        {
            var result = new HashSet<string>();

            foreach (string typeName in TypeDefinitions.Keys)
            {
                int index = typeName.LastIndexOf('.');

                if (index >= 0)
                {
                    RegisterNamespace(typeName.Substring(0, index), result);
                }
            }

            return result;
        }

        protected void RegisterNamespace(string ns, ICollection<string> repository)
        {
            if (String.IsNullOrEmpty(ns) || repository.Contains(ns))
            {
                return;
            }

            string[] parts = ns.Split('.');
            StringBuilder builder = new StringBuilder();

            foreach (string part in parts)
            {
                if (builder.Length > 0)
                {
                    builder.Append('.');
                }

                builder.Append(part);
                string item = builder.ToString();

                if (!repository.Contains(item))
                {
                    repository.Add(item);
                }
            }
        }

        public static object ConvertConstant(object value, Expression expression, IEmitter emitter)
        {
            try
            {
                if (expression.Parent != null)
                {
                    var rr = emitter.Resolver.ResolveNode(expression);
                    var conversion = emitter.Resolver.Resolver.GetConversion(expression);
                    var expectedType = emitter.Resolver.Resolver.GetExpectedType(expression);

                    if (conversion.IsNumericConversion && expectedType.IsKnownType(KnownTypeCode.Double) && rr.Type.IsKnownType(KnownTypeCode.Single))
                    {
                        return (double)(float)value;
                    }
                }
            }
            catch (Exception)
            {
            }

            return value;
        }

        public string ToJavaScript(object value)
        {
            return JsonConvert.SerializeObject(value, _settings);
        }

        private static readonly JsonSerializerSettings _settings = new JsonSerializerSettings { StringEscapeHandling = StringEscapeHandling.EscapeNonAscii };

        protected ICSharpCode.NRefactory.CSharp.Attribute GetAttribute(AstNodeCollection<AttributeSection> attributes, string name)
        {
            string fullName = name + "Attribute";
            foreach (var i in attributes)
            {
                foreach (var j in i.Attributes)
                {
                    if (j.Type.ToString() == name)
                    {
                        return j;
                    }

                    var resolveResult = Resolver.ResolveNode(j);
                    if (resolveResult != null && resolveResult.Type != null && resolveResult.Type.FullName == fullName)
                    {
                        return j;
                    }
                }
            }

            return null;
        }

        public CustomAttribute GetAttribute(IEnumerable<CustomAttribute> attributes, string name)
        {
            foreach (var attr in attributes)
            {
                if (attr.AttributeType.FullName == name)
                {
                    return attr;
                }
            }

            return null;
        }

        public IAttribute GetAttribute(IEnumerable<IAttribute> attributes, string name)
        {
            foreach (var attr in attributes)
            {
                if (attr.AttributeType.FullName == name)
                {
                    return attr;
                }
            }

            return null;
        }

        protected bool HasDelegateAttribute(MethodDeclaration method)
        {
            return GetAttribute(method.Attributes, "Delegate") != null;
        }

        public Tuple<bool, bool, string> GetInlineCode(MemberReferenceExpression node)
        {
            var member = LiftNullableMember(node);
            var info = GetInlineCodeFromMember(member, node);

            return WrapNullableMember(info, member, node);
        }

        public Tuple<bool, bool, string> GetInlineCode(InvocationExpression node)
        {
            IMember member = null;
            if (node.Target is MemberReferenceExpression target)
            {
                member = LiftNullableMember(target);
            }

            var info = GetInlineCodeFromMember(member, node);
            return WrapNullableMember(info, member, node.Target);
        }

        internal Tuple<bool, bool, string> GetInlineCodeFromMember(IMember member, Expression node)
        {
            if (member == null)
            {
                var resolveResult = Resolver.ResolveNode(node);

                if (!(resolveResult is MemberResolveResult memberResolveResult))
                {
                    return new Tuple<bool, bool, string>(false, false, null);
                }

                member = memberResolveResult.Member;
            }

            bool isInlineMethod = IsInlineMethod(member);
            var inlineCode = isInlineMethod ? null : GetInline(member);
            var isStatic = member.IsStatic;

            if (!string.IsNullOrEmpty(inlineCode) && member is IProperty)
            {
                inlineCode = inlineCode.Replace("{value}", "{0}");
            }

            return new Tuple<bool, bool, string>(isStatic, isInlineMethod, inlineCode);
        }

        private Tuple<bool, bool, string> WrapNullableMember(Tuple<bool, bool, string> info, IMember member, Expression node)
        {
            if (member != null && !string.IsNullOrEmpty(info.Item3))
            {
                IMethod method = (IMethod)member;

                StringBuilder savedBuilder = Output;
                Output = new StringBuilder();
                var mrr = new MemberResolveResult(null, member);
                var argsInfo = new ArgumentsInfo(this, node, mrr);
                argsInfo.ThisArgument = JS.Vars.T;
                new InlineArgumentsBlock(this, argsInfo, info.Item3, method, mrr).EmitNullableReference();
                string tpl = Output.ToString();
                Output = savedBuilder;

                if (member.Name == CS.Methods.EQUALS)
                {
                    tpl = string.Format(JS.Types.SYSTEM_NULLABLE + "." + JS.Funcs.EQUALS + "({{this}}, {{{0}}}, {1})", method.Parameters.First().Name, tpl);
                }
                else if (member.Name == CS.Methods.TOSTRING)
                {
                    tpl = string.Format(JS.Types.SYSTEM_NULLABLE + "." + JS.Funcs.TOSTIRNG + "({{this}}, {0})", tpl);
                }
                else if (member.Name == CS.Methods.GETHASHCODE)
                {
                    tpl = string.Format(JS.Types.SYSTEM_NULLABLE + "." + JS.Funcs.GETHASHCODE + "({{this}}, {0})", tpl);
                }

                info = new Tuple<bool, bool, string>(info.Item1, info.Item2, tpl);
            }
            return info;
        }

        private IMember LiftNullableMember(MemberReferenceExpression target)
        {
            var targetrr = Resolver.ResolveNode(target.Target);
            IMember member = null;
            if (targetrr.Type.IsKnownType(KnownTypeCode.NullableOfT))
            {
                string name = null;
                int count = 0;
                IType typeArg = null;
                if (target.MemberName == CS.Methods.TOSTRING || target.MemberName == CS.Methods.GETHASHCODE)
                {
                    name = target.MemberName;
                }
                else if (target.MemberName == CS.Methods.EQUALS)
                {
                    if (target.Parent is InvocationExpression)
                    {
                        if (Resolver.ResolveNode(target.Parent) is InvocationResolveResult rr)
                        {
                            typeArg = rr.Arguments.First().Type;
                        }
                    }
                    name = target.MemberName;
                    count = 1;
                }

                if (name != null)
                {
                    var type = ((ParameterizedType)targetrr.Type).TypeArguments[0];
                    var methods = type.GetMethods(null, GetMemberOptions.IgnoreInheritedMembers);

                    if (count == 0)
                    {
                        member = methods.FirstOrDefault(m => m.Name == name && m.Parameters.Count == count);
                    }
                    else
                    {
                        member = methods.FirstOrDefault(m => m.Name == name && m.Parameters.Count == count && m.Parameters.First().Type.Equals(typeArg));

                        if (member == null)
                        {
                            var typeDef = typeArg.GetDefinition();
                            member = methods.FirstOrDefault(m => m.Name == name && m.Parameters.Count == count && m.Parameters.First().Type.GetDefinition().IsDerivedFrom(typeDef));
                        }
                    }
                }
            }
            return member;
        }

        public bool IsForbiddenInvocation(InvocationExpression node)
        {
            var resolveResult = Resolver.ResolveNode(node);

            if (!(resolveResult is MemberResolveResult memberResolveResult))
            {
                return false;
            }

            var member = memberResolveResult.Member;

            string attrName = H5.Translator.Translator.H5_ASSEMBLY + ".InitAttribute";

            if (member != null)
            {
                var attr = member.Attributes.FirstOrDefault(a =>
                {
                    return a.AttributeType.FullName == attrName;
                });

                if (attr != null)
                {
                    if (attr.PositionalArguments.Count > 0)
                    {
                        var argExpr = attr.PositionalArguments.First();
                        if (argExpr.ConstantValue is int)
                        {
                            var value = (InitPosition)argExpr.ConstantValue;

                            if (value > 0)
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        public IEnumerable<string> GetScript(EntityDeclaration method)
        {
            var attr = GetAttribute(method.Attributes, H5.Translator.Translator.H5_ASSEMBLY + ".Script");

            return GetScriptArguments(attr);
        }

        public string GetEntityNameFromAttr(IEntity member, bool setter = false)
        {
            if (member is IProperty prop)
            {
                member = setter ? prop.Setter : prop.Getter;
            }
            else
            {
                if (member is IEvent e)
                {
                    member = setter ? e.AddAccessor : e.RemoveAccessor;
                }
            }

            if (member == null)
            {
                return null;
            }

            var attr = Helpers.GetInheritedAttribute(member, H5.Translator.Translator.H5_ASSEMBLY + ".NameAttribute");
            bool isIgnore = member.DeclaringTypeDefinition != null && Validator.IsExternalType(member.DeclaringTypeDefinition);
            string name;

            if (attr != null)
            {
                var value = attr.PositionalArguments.First().ConstantValue;
                if (value is string)
                {
                    name = GetEntityName(member);
                    if (!isIgnore && member.IsStatic && Helpers.IsReservedStaticName(name, false))
                    {
                        name = Helpers.ChangeReservedWord(name);
                    }
                    return name;
                }
            }

            return null;
        }

        Dictionary<IEntity, NameSemantic> entityNameCache = new Dictionary<IEntity, NameSemantic>();
        public NameSemantic GetNameSemantic(IEntity member)
        {
            NameSemantic result;
            if (entityNameCache.TryGetValue(member, out result))
            {
                return result;
            }

            result = new NameSemantic { Entity = member, Emitter = this };

            entityNameCache.Add(member, result);
            return result;
        }

        public string GetEntityName(IEntity member)
        {
            var semantic = NameSemantic.Create(member, this);
            semantic.IsObjectLiteral = false;
            return semantic.Name;
        }

        public string GetTypeName(ITypeDefinition type, TypeDefinition typeDefinition)
        {
            var semantic = NameSemantic.Create(type, this);
            semantic.TypeDefinition = typeDefinition;
            return semantic.Name;
        }

        public string GetLiteralEntityName(IEntity member)
        {
            var semantic = NameSemantic.Create(member, this);
            semantic.IsObjectLiteral = true;
            return semantic.Name;
        }

        public string GetEntityName(EntityDeclaration entity)
        {
            if (Resolver.ResolveNode(entity) is MemberResolveResult rr)
            {
                return GetEntityName(rr.Member);
            }

            return null;
        }

        public string GetParameterName(ParameterDeclaration entity)
        {
            var name = entity.Name;

            if (entity.Parent != null && entity.GetParent<SyntaxTree>() != null)
            {
                if (Resolver.ResolveNode(entity) is LocalResolveResult rr)
                {
                    if (rr.Variable is IParameter iparam && iparam.Attributes != null)
                    {
                        var attr = iparam.Attributes.FirstOrDefault(a => a.AttributeType.FullName == H5.Translator.Translator.H5_ASSEMBLY + ".NameAttribute");

                        if (attr != null)
                        {
                            var value = attr.PositionalArguments.First().ConstantValue;
                            if (value is string)
                            {
                                name = value.ToString();
                            }
                        }
                    }
                }
            }

            if (Helpers.IsReservedWord(this, name))
            {
                name = Helpers.ChangeReservedWord(name);
            }

            return name;
        }

        public string GetFieldName(FieldDeclaration field)
        {
            if (!string.IsNullOrEmpty(field.Name))
            {
                return field.Name;
            }

            if (field.Variables.Count > 0)
            {
                return field.Variables.First().Name;
            }

            return null;
        }

        public string GetEventName(EventDeclaration evt)
        {
            if (!string.IsNullOrEmpty(evt.Name))
            {
                return evt.Name;
            }

            if (evt.Variables.Count > 0)
            {
                return evt.Variables.First().Name;
            }

            return null;
        }

        public Tuple<bool, string> IsGlobalTarget(IMember member)
        {
            var attr = GetAttribute(member.Attributes, H5.Translator.Translator.H5_ASSEMBLY + ".GlobalTargetAttribute");

            return attr != null ? new Tuple<bool, string>(true, (string)attr.PositionalArguments.First().ConstantValue) : null;
        }

        public string GetInline(EntityDeclaration method)
        {
            if (Resolver.ResolveNode(method) is MemberResolveResult mrr)
            {
                return GetInline(mrr.Member);
            }

            var attr = GetAttribute(method.Attributes, H5.Translator.Translator.H5_ASSEMBLY + ".Template");

            return attr != null && attr.Arguments.Count > 0 ? ((string)((PrimitiveExpression)attr.Arguments.First()).Value) : null;
        }

        public string GetInline(IEntity entity)
        {
            string attrName = H5.Translator.Translator.H5_ASSEMBLY + ".TemplateAttribute";
            // Moving these two `is` into the end of the methos (where it's actually used) leads
            // to incorrect JavaScript being generated
            bool isProp = entity is IProperty;
            bool isEvent = entity is IEvent;

            if (entity.SymbolKind == SymbolKind.Property)
            {
                var prop = (IProperty)entity;
                entity = IsAssignment ? prop.Setter : prop.Getter;
            }
            else if (entity.SymbolKind == SymbolKind.Event)
            {
                var ev = (IEvent)entity;
                entity = IsAssignment ? (AssignmentType == AssignmentOperatorType.Add ? ev.AddAccessor : ev.RemoveAccessor) : ev.InvokeAccessor;
            }

            if (entity != null)
            {
                var attr = entity.Attributes.FirstOrDefault(a =>
                {
                    return a.AttributeType.FullName == attrName;
                });

                string inlineCode = null;
                if (attr != null && entity is IMethod method && attr.PositionalArguments.Count == 0 && attr.NamedArguments.Count > 0)
                {
                    var namedArg = attr.NamedArguments.FirstOrDefault(arg => arg.Key.Name == CS.Attributes.Template.PROPERTY_FN);
                    if (namedArg.Value != null)
                    {
                        inlineCode = namedArg.Value.ConstantValue as string;

                        if (inlineCode != null)
                        {
                            inlineCode = Helpers.DelegateToTemplate(inlineCode, method, this);
                        }
                    }
                }

                if (inlineCode == null)
                {
                    inlineCode = attr != null && attr.PositionalArguments.Count > 0 ? attr.PositionalArguments[0].ConstantValue.ToString() : null;
                }

                if (!string.IsNullOrEmpty(inlineCode) && (isProp || isEvent))
                {
                    inlineCode = inlineCode.Replace("{value}", "{0}");
                }

                return inlineCode;
            }

            return null;
        }

        protected bool IsInlineMethod(IEntity entity)
        {
            const string attrName = H5.Translator.Translator.H5_ASSEMBLY + ".TemplateAttribute";

            if (entity != null)
            {
                var attr = entity.Attributes.FirstOrDefault(a =>
                {
                    return a.AttributeType.FullName == attrName;
                });

                return attr != null && attr.PositionalArguments.Count == 0 && attr.NamedArguments.Count == 0;
            }

            return false;
        }

        protected IEnumerable<string> GetScriptArguments(ICSharpCode.NRefactory.CSharp.Attribute attr)
        {
            if (attr == null)
            {
                return null;
            }

            var result = new List<string>();

            foreach (var arg in attr.Arguments)
            {
                string value = "";
                if (arg is PrimitiveExpression expr)
                {
                    value = (string)expr.Value;
                }
                else
                {
                    if (Resolver.ResolveNode(arg) is ConstantResolveResult rr && rr.ConstantValue != null)
                    {
                        value = rr.ConstantValue.ToString();
                    }
                }

                result.Add(value);
            }

            return result;
        }

        public bool IsNativeMember(string fullName)
        {
            return fullName.StartsWith(H5.Translator.Translator.H5_ASSEMBLY_DOT, StringComparison.Ordinal) || fullName.StartsWith("System.", StringComparison.Ordinal);
        }

        public bool IsMemberConst(IMember member)
        {
            if (member is IField field)
            {
                return field.IsConst && member.DeclaringType.Kind != TypeKind.Enum;
            }

            return false;
        }

        public bool IsInlineConst(IMember member)
        {
            bool isConst = IsMemberConst(member);

            if (isConst)
            {
                var attr = GetAttribute(member.Attributes, H5.Translator.Translator.H5_ASSEMBLY + ".InlineConstAttribute");

                if (attr != null)
                {
                    return true;
                }
            }

            return false;
        }

        public void InitEmitter()
        {
            Output = new StringBuilder();
            Locals = null;
            LocalsStack = null;
            IteratorCount = 0;
            ThisRefCounter = 0;
            Writers = new Stack<IWriter>();
            IsAssignment = false;
            ResetLevel();
            IsNewLine = true;
            EnableSemicolon = true;
            Comma = false;
            CurrentDependencies = new List<IModuleDependency>();
        }

        public bool ContainsOnlyOrEmpty(StringBuilder sb, params char[] c)
        {
            if (sb == null || sb.Length == 0)
            {
                return true;
            }

            for (int i = 0; i < sb.Length; i++)
            {
                if (!c.Contains(sb[i]))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool AddOutputItem(List<TranslatorOutputItem> target, string fileName, TranslatorOutputItemContent content, TranslatorOutputKind outputKind, string location = null, string assembly = null)
        {
            var fileHelper = new FileHelper();

            var outputType = fileHelper.GetOutputType(fileName);

            TranslatorOutputItem output = null;

            bool isMinJs = fileHelper.IsMinJS(fileName);

            var searchName = fileName;

            if (isMinJs)
            {
                searchName = fileHelper.GetNonMinifiedJSFileName(fileName);
            }

            output = target.FirstOrDefault(x => string.Compare(x.Name, searchName, StringComparison.InvariantCultureIgnoreCase) == 0);

            if (output != null)
            {
                bool isAdded;

                if (isMinJs)
                {
                    isAdded = output.MinifiedVersion == null;

                    output.MinifiedVersion = new TranslatorOutputItem
                    {
                        Name = fileName,
                        OutputType = outputType,
                        OutputKind = outputKind | TranslatorOutputKind.Minified,
                        Location = location,
                        Content = content,
                        IsMinified = true,
                        Assembly = assembly
                    };
                }
                else
                {
                    isAdded = output.IsEmpty;
                    output.IsEmpty = false;
                }

                return isAdded;
            }

            output = new TranslatorOutputItem
            {
                Name = searchName,
                OutputType = outputType,
                OutputKind = outputKind,
                Location = location,
                Content = new TranslatorOutputItemContent((string)null),
                Assembly = assembly
            };

            if (isMinJs)
            {
                output.IsEmpty = true;

                output.MinifiedVersion = new TranslatorOutputItem
                {
                    Name = fileName,
                    OutputType = outputType,
                    OutputKind = outputKind | TranslatorOutputKind.Minified,
                    Location = location,
                    Content = content,
                    IsMinified = true,
                    Assembly = assembly
                };
            }
            else
            {
                output.Content = content;
            }

            target.Add(output);

            return true;
        }
    }
}