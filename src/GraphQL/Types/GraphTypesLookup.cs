using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GraphQL.Conversion;
using GraphQL.Introspection;
using GraphQL.Types.Relay;
using GraphQL.Utilities;

namespace GraphQL.Types
{
    /// <summary>
    /// Provides lookup for all schema types and has algorithms for discovering them.
    /// </summary>
    public class GraphTypesLookup
    {
        // Introspection types http://spec.graphql.org/June2018/#sec-Schema-Introspection
        private readonly Dictionary<Type, IGraphType> _introspectionTypes = new IGraphType[]
        {
            new __DirectiveLocation(),
            new __TypeKind(),
            new __EnumValue(),
            new __Directive(),
            new __Field(),
            new __InputValue(),
            new __Type(),
            new __Schema()
        }
        .ToDictionary(t => t.GetType());

        // Standard scalars https://graphql.github.io/graphql-spec/June2018/#sec-Scalars
        private readonly Dictionary<Type, IGraphType> _builtInScalars = new IGraphType[]
        {
            new StringGraphType(),
            new BooleanGraphType(),
            new FloatGraphType(),
            new IntGraphType(),
            new IdGraphType(),
        }
        .ToDictionary(t => t.GetType());

        // .NET custom scalars
        private readonly Dictionary<Type, IGraphType> _builtInCustomScalars = new IGraphType[]
        {
            new DateGraphType(),
            new DateTimeGraphType(),
            new DateTimeOffsetGraphType(),
            new TimeSpanSecondsGraphType(),
            new TimeSpanMillisecondsGraphType(),
            new DecimalGraphType(),
            new UriGraphType(),
            new GuidGraphType(),
            new ShortGraphType(),
            new UShortGraphType(),
            new UIntGraphType(),
            new LongGraphType(),
            new BigIntGraphType(),
            new ULongGraphType(),
            new ByteGraphType(),
            new SByteGraphType(),
        }
        .ToDictionary(t => t.GetType());

        private readonly IDictionary<string, IGraphType> _types = new Dictionary<string, IGraphType>();
        private readonly TypeCollectionContext _context;
        private readonly object _lock = new object();
        private bool _sealed;

        /// <summary>
        /// Initializes a new instance with the <see cref="CamelCaseNameConverter"/>.
        /// </summary>
        public GraphTypesLookup() : this(CamelCaseNameConverter.Instance) { }

        /// <summary>
        /// Initalizes a new instance with the specified <see cref="INameConverter"/>.
        /// </summary>
        public GraphTypesLookup(INameConverter nameConverter)
        {
#pragma warning disable IDE0016 // Use 'throw' expression; if this rule is applied here, then the null check is moved to the very end of the method - this is not what we want
            if (nameConverter == null)
                throw new ArgumentNullException(nameof(nameConverter));
#pragma warning restore IDE0016

            _context = new TypeCollectionContext(
               type => BuildNamedType(type, t => _builtInScalars.TryGetValue(t, out var graphType) ? graphType : _introspectionTypes.TryGetValue(t, out graphType) ? graphType : (IGraphType)Activator.CreateInstance(t)),
               (name, type, ctx) =>
               {
                   lock (_lock)
                   {
                       SetGraphType(name, type);
                   }
                   ctx.AddType(name, type, null);
               });

            // Add introspection types. Note that introspection types rely on the
            // CamelCaseNameConverter, as some fields are defined in pascal case - e.g. Field(x => x.Name)
            NameConverter = CamelCaseNameConverter.Instance;

            foreach (var introspectionType in _introspectionTypes.Values)
                AddType(introspectionType);

            // set the name converter properly
            NameConverter = nameConverter;
        }

        private void CheckSealed()
        {
            if (_sealed)
                throw new InvalidOperationException("GraphTypesLookup is sealed for modifications. You attempt to modify schema after it was initialized.");
        }

        private IGraphType BuildNamedType(Type type, Func<Type, IGraphType> resolver) => type.BuildNamedType(t => this[t] ?? resolver(t));

        /// <summary>
        /// Initializes a new instance for the specified graph types and directives, and with the specified type resolver and name converter.
        /// </summary>
        /// <param name="types">A list of graph type instances to register in the lookup table.</param>
        /// <param name="directives">A list of directives to register.</param>
        /// <param name="resolveType">A delegate which returns an instance of a graph type from its .NET type.</param>
        /// <param name="nameConverter">A name converter to use for the specified graph types.</param>
        /// <param name="seal">Prevents additional types from being added to the lookup table.</param>
        public static GraphTypesLookup Create(
            IEnumerable<IGraphType> types,
            IEnumerable<DirectiveGraphType> directives,
            Func<Type, IGraphType> resolveType,
            INameConverter nameConverter,
            bool seal = false)
        {
            var lookup = nameConverter == null ? new GraphTypesLookup() : new GraphTypesLookup(nameConverter);

            var ctx = new TypeCollectionContext(t => lookup._builtInScalars.TryGetValue(t, out var graphType) ? graphType : resolveType(t), (name, graphType, context) =>
            {
                if (lookup[name] == null)
                {
                    lookup.AddType(graphType, context);
                }
            });

            foreach (var type in types)
            {
                lookup.AddType(type, ctx);
            }

            // these fields must not have their field names translated by INameConverter; see HandleField
            lookup.HandleField(null, lookup.SchemaMetaFieldType, ctx, false);
            lookup.HandleField(null, lookup.TypeMetaFieldType, ctx, false);
            lookup.HandleField(null, lookup.TypeNameMetaFieldType, ctx, false);

            foreach (var directive in directives)
            {
                if (directive.Arguments == null)
                    continue;

                foreach (var arg in directive.Arguments)
                {
                    if (arg.ResolvedType != null)
                    {
                        lookup.AddTypeIfNotRegistered(arg.ResolvedType, ctx);
                        arg.ResolvedType = lookup.ConvertTypeReference(directive, arg.ResolvedType);
                    }
                    else
                    {
                        lookup.AddTypeIfNotRegistered(arg.Type, ctx);
                        arg.ResolvedType = lookup.BuildNamedType(arg.Type, ctx.ResolveType);
                    }
                }
            }

            lookup.ApplyTypeReferences();

            Debug.Assert(ctx.InFlightRegisteredTypes.Count == 0);
            lookup._sealed = seal;

            return lookup;
        }

        /// <summary>
        /// Gets or sets the name converter used when adding types to the lookup table.
        /// </summary>
        public INameConverter NameConverter { get; set; }

        internal void Clear(bool internalCall)
        {
            if (!internalCall)
                CheckSealed();

            lock (_lock)
            {
                _types.Clear();
            }
        }

        /// <summary>
        /// Removes all discovered types from lookup. 
        /// </summary>
        public void Clear() => Clear(false);

        /// <summary>
        /// Returns a list of all of the discovered types from the lookup table.
        /// </summary>
        public IEnumerable<IGraphType> All()
        {
            lock (_lock)
            {
                return _types.Values.ToList();
            }
        }

        /// <summary>
        /// Returns a graph type instance from the lookup table by its GraphQL type name.
        /// </summary>
        public IGraphType this[string typeName]
        {
            get
            {
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    throw new ArgumentOutOfRangeException(nameof(typeName), "A type name is required to lookup.");
                }

                IGraphType type;
                lock (_lock)
                {
                    _types.TryGetValue(typeName, out type);
                }
                return type;
            }
            set
            {
                CheckSealed();

                lock (_lock)
                {
                    SetGraphType(typeName, value);
                }
            }
        }

        /// <summary>
        /// Returns a graph type instance from the lookup table by its .NET type.
        /// </summary>
        /// <param name="type">The .NET type of the graph type.</param>
        public IGraphType this[Type type]
        {
            get
            {
                lock (_lock)
                {
                    var result = _types.FirstOrDefault(x => x.Value.GetType() == type);
                    return result.Value;
                }
            }
        }

        /// <summary>
        /// Adds the specified GraphType to lookup. The instance of this type will be resolved by resolveType parameter specified in
        /// <see cref="Create(IEnumerable{IGraphType}, IEnumerable{DirectiveGraphType}, Func{Type, IGraphType}, INameConverter, bool)"/>
        /// method when creating this lookup.
        /// </summary>
        /// <typeparam name="TType">The graph type to add.</typeparam>
        public void AddType<TType>()
            where TType : IGraphType
        {
            CheckSealed();

            var type = typeof(TType).GetNamedType();
            var instance = _context.ResolveType(type);
            AddType(instance);

            Debug.Assert(_context.InFlightRegisteredTypes.Count == 0);
        }

        /// <summary>
        /// Adds the specified GraphType to lookup.
        /// </summary>
        /// <param name="type"> GraphType to add. </param>
        public void AddType(IGraphType type) => AddType(type, _context);

        private void AddType(IGraphType type, TypeCollectionContext context)
        {
            CheckSealed();

            if (type == null || type is GraphQLTypeReference)
            {
                return;
            }

            if (type is NonNullGraphType || type is ListGraphType)
            {
                throw new ArgumentOutOfRangeException(nameof(type), "Only add root types.");
            }

            string name = context.CollectTypes(type);
            lock (_lock)
            {
                SetGraphType(name, type);
            }

            if (type is IComplexGraphType complexType)
            {
                foreach (var field in complexType.Fields)
                {
                    HandleField(complexType, field, context, true);
                }
            }

            if (type is IObjectGraphType obj)
            {
                foreach (var objectInterface in obj.Interfaces)
                {
                    AddTypeIfNotRegistered(objectInterface, context);

                    if (this[objectInterface] is IInterfaceGraphType interfaceInstance)
                    {
                        obj.AddResolvedInterface(interfaceInstance);
                        interfaceInstance.AddPossibleType(obj);

                        if (interfaceInstance.ResolveType == null && obj.IsTypeOf == null)
                        {
                            throw new InvalidOperationException(
                               $"Interface type \"{interfaceInstance.Name}\" does not provide a \"resolveType\" function " +
                               $"and possible Type \"{obj.Name}\" does not provide a \"isTypeOf\" function. " +
                                "There is no way to resolve this possible type during execution.");
                        }
                    }
                }
            }

            if (type is UnionGraphType union)
            {
                if (!union.Types.Any() && !union.PossibleTypes.Any())
                {
                    throw new InvalidOperationException($"Must provide types for Union '{union}'.");
                }

                foreach (var unionedType in union.PossibleTypes)
                {
                    // skip references
                    if (unionedType is GraphQLTypeReference)
                        continue;

                    AddTypeIfNotRegistered(unionedType, context);

                    if (union.ResolveType == null && unionedType.IsTypeOf == null)
                    {
                        throw new InvalidOperationException(
                           $"Union type \"{union.Name}\" does not provide a \"resolveType\" function " +
                           $"and possible Type \"{unionedType.Name}\" does not provide a \"isTypeOf\" function. " +
                            "There is no way to resolve this possible type during execution.");
                    }
                }

                foreach (var unionedType in union.Types)
                {
                    AddTypeIfNotRegistered(unionedType, context);

                    var objType = this[unionedType] as IObjectGraphType;

                    if (union.ResolveType == null && objType != null && objType.IsTypeOf == null)
                    {
                        throw new InvalidOperationException(
                           $"Union type \"{union.Name}\" does not provide a \"resolveType\" function " +
                           $"and possible Type \"{objType.Name}\" does not provide a \"isTypeOf\" function. " +
                            "There is no way to resolve this possible type during execution.");
                    }

                    union.AddPossibleType(objType);
                }
            }
        }

        private void HandleField(IComplexGraphType parentType, FieldType field, TypeCollectionContext context, bool applyNameConverter)
        {
            // applyNameConverter will be false while processing the three root introspection query fields: __schema, __type, and __typename
            //
            // During processing of those three root fields, the NameConverter will be set to the schema's selected NameConverter,
            //   and the field names must not be processed by the NameConverter
            //
            // For other introspection types and fields, the NameConverter will be set to CamelCaseNameConverter at the time this
            //   code executes, and applyNameConverter will be true
            //
            // For any other fields, the NameConverter will be set to the schema's selected NameConverter at the time this code
            //   executes, and applyNameConverter will be true

            if (applyNameConverter)
            {
                field.Name = NameConverter.NameForField(field.Name, parentType);
                NameValidator.ValidateNameOnSchemaInitialize(field.Name);
            }

            if (field.ResolvedType == null)
            {
                AddTypeIfNotRegistered(field.Type, context);
                field.ResolvedType = BuildNamedType(field.Type, context.ResolveType);
            }
            else
            {
                AddTypeIfNotRegistered(field.ResolvedType, context);
            }

            if (field.Arguments == null)
                return;

            foreach (var arg in field.Arguments)
            {
                if (applyNameConverter)
                {
                    arg.Name = NameConverter.NameForArgument(arg.Name, parentType, field);
                    NameValidator.ValidateNameOnSchemaInitialize(arg.Name, "argument");
                }

                if (arg.ResolvedType != null)
                {
                    AddTypeIfNotRegistered(arg.ResolvedType, context);
                    continue;
                }

                AddTypeIfNotRegistered(arg.Type, context);
                arg.ResolvedType = BuildNamedType(arg.Type, context.ResolveType);
            }
        }

        // https://github.com/graphql-dotnet/graphql-dotnet/pull/1010
        private void AddTypeWithLoopCheck(IGraphType resolvedType, TypeCollectionContext context, Type namedType)
        {
            if (context.InFlightRegisteredTypes.Any(t => t == namedType))
                throw new InvalidOperationException($@"A loop has been detected while registering schema types.
There was an attempt to re-register '{namedType.FullName}' with instance of '{resolvedType.GetType().FullName}'.
Make sure that your ServiceProvider is configured correctly.");

            context.InFlightRegisteredTypes.Push(namedType);
            try
            {
                AddType(resolvedType, context);
            }
            finally
            {
                context.InFlightRegisteredTypes.Pop();
            }
        }

        private void AddTypeIfNotRegistered(Type type, TypeCollectionContext context)
        {
            var namedType = type.GetNamedType();
            var foundType = this[namedType];
            if (foundType == null)
            {
                if (namedType == typeof(PageInfoType))
                {
                    AddType(new PageInfoType(), context);
                }
                else if (namedType.IsGenericType && (namedType.ImplementsGenericType(typeof(EdgeType<>)) || namedType.ImplementsGenericType(typeof(ConnectionType<,>))))
                {
                    AddType((IGraphType)Activator.CreateInstance(namedType), context);
                }
                else if (_builtInCustomScalars.TryGetValue(namedType, out var builtInCustomScalar))
                {
                    AddType(builtInCustomScalar, _context);
                }
                else
                {
                    AddTypeWithLoopCheck(context.ResolveType(namedType), context, namedType);
                }
            }
        }

        private void AddTypeIfNotRegistered(IGraphType type, TypeCollectionContext context)
        {
            var namedType = type.GetNamedType();
            var foundType = this[namedType.Name];
            if (foundType == null)
            {
                AddType(namedType, context);
            }
        }

        private void ApplyTypeReferences()
        {
            CheckSealed();

            foreach (var type in _types.Values.ToList())
            {
                ApplyTypeReference(type);
            }
        }

        private void ApplyTypeReference(IGraphType type)
        {
            CheckSealed();

            if (type is IComplexGraphType complexType)
            {
                foreach (var field in complexType.Fields)
                {
                    field.ResolvedType = ConvertTypeReference(type, field.ResolvedType);

                    if (field.Arguments == null)
                        continue;

                    foreach (var arg in field.Arguments)
                    {
                        arg.ResolvedType = ConvertTypeReference(type, arg.ResolvedType);
                    }
                }
            }

            if (type is IObjectGraphType objectType)
            {
                var list = objectType.ResolvedInterfaces.List;
                for (int i = 0; i < list.Count; ++i)
                {
                    var interfaceType = (IInterfaceGraphType)ConvertTypeReference(objectType, list[i]);

                    if (objectType.IsTypeOf == null && interfaceType.ResolveType == null)
                    {
                        throw new InvalidOperationException(
                               $"Interface type \"{interfaceType.Name}\" does not provide a \"resolveType\" function " +
                               $"and possible Type \"{objectType.Name}\" does not provide a \"isTypeOf\" function.  " +
                                "There is no way to resolve this possible type during execution.");
                    }

                    interfaceType.AddPossibleType(objectType);

                    list[i] = interfaceType;
                }
            }

            if (type is UnionGraphType union)
            {
                var list = union.PossibleTypes.List;
                for (int i=0; i<list.Count; ++i)
                {
                    var unionType = ConvertTypeReference(union, list[i]) as IObjectGraphType;

                    if (union.ResolveType == null && unionType != null && unionType.IsTypeOf == null)
                    {
                        throw new InvalidOperationException(
                           $"Union type \"{union.Name}\" does not provide a \"resolveType\" function " +
                           $"and possible Type \"{union.Name}\" does not provide a \"isTypeOf\" function. " +
                            "There is no way to resolve this possible type during execution.");
                    }

                    list[i] = unionType;
                }
            }
        }

        private IGraphType ConvertTypeReference(INamedType parentType, IGraphType type)
        {
            if (type is NonNullGraphType nonNull)
            {
                nonNull.ResolvedType = ConvertTypeReference(parentType, nonNull.ResolvedType);
                return nonNull;
            }

            if (type is ListGraphType list)
            {
                list.ResolvedType = ConvertTypeReference(parentType, list.ResolvedType);
                return list;
            }

            var reference = type as GraphQLTypeReference;
            if (reference != null)
            {
                type = this[reference.TypeName];
                if (type == null)
                {
                    type = _builtInScalars.Values.FirstOrDefault(t => t.Name == reference.TypeName) ?? _builtInCustomScalars.Values.FirstOrDefault(t => t.Name == reference.TypeName);
                    if (type != null)
                        this[type.Name] = type;
                }
            }

            if (reference != null && type == null)
            {
                throw new InvalidOperationException($"Unable to resolve reference to type '{reference.TypeName}' on '{parentType.Name}'");
            }

            return type;
        }

        private void SetGraphType(string typeName, IGraphType type)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                throw new ArgumentOutOfRangeException(nameof(typeName), "A type name is required to lookup.");
            }

            if (_types.TryGetValue(typeName, out var existingGraphType))
            {
                if (ReferenceEquals(existingGraphType, type))
                {
                    // nothing to do
                }
                else if (existingGraphType.GetType() == type.GetType())
                {
                    _types[typeName] = type; // this case worked before overwriting the old value
                }
                else
                {
                    throw new InvalidOperationException($@"Unable to register GraphType '{type.GetType().FullName}' with the name '{typeName}';
the name '{typeName}' is already registered to '{existingGraphType.GetType().FullName}'.");
                }
            }
            else
            {
                _types.Add(typeName, type);
            }
        }

        /// <summary>
        /// Returns the <see cref="FieldType"/> instance for the <c>__schema</c> meta-field.
        /// </summary>
        public FieldType SchemaMetaFieldType { get; } = new SchemaMetaFieldType();

        /// <summary>
        /// Returns the <see cref="FieldType"/> instance for the <c>__type</c> meta-field.
        /// </summary>
        public FieldType TypeMetaFieldType { get; } = new TypeMetaFieldType();

        /// <summary>
        /// Returns the <see cref="FieldType"/> instance for the <c>__typename</c> meta-field.
        /// </summary>
        public FieldType TypeNameMetaFieldType { get; } = new TypeNameMetaFieldType();
    }
}
