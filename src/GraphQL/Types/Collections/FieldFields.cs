using System;
using System.Collections;
using System.Collections.Generic;

namespace GraphQL.Types
{
    /// <summary>
    /// A class that represents a set of fields for <see cref="IComplexGraphType"/> i.e <see cref="ComplexGraphType{TSourceType}"/>.
    /// </summary>
    public class FieldFields : IEnumerable<FieldType>
    {
        internal List<FieldType> List { get; } = new List<FieldType>();

        /// <summary>
        /// Gets the count of fields.
        /// </summary>
        public int Count => List.Count;

        internal void Add(FieldType field)
        {
            if (field == null)
                throw new ArgumentNullException(nameof(field));

            if (!List.Contains(field))
                List.Add(field);
        }

        public bool Contains(FieldType field) => List.Contains(field ?? throw new ArgumentNullException(nameof(field)));

        public bool Contains(IFieldType field) => List.Contains((FieldType)field ?? throw new ArgumentNullException(nameof(field)));

        /// <inheritdoc cref="IEnumerable.GetEnumerator"/>
        public IEnumerator<FieldType> GetEnumerator() => List.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
