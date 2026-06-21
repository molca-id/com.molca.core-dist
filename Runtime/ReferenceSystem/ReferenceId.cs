using System;
using UnityEngine;

namespace Molca.ReferenceSystem
{
    /// <summary>
    /// A type-safe reference ID that encapsulates both the ID string and the reference type.
    /// Provides compile-time safety and prevents type confusion when working with references.
    /// </summary>
    [Serializable]
    public readonly struct ReferenceId : IEquatable<ReferenceId>, IComparable<ReferenceId>
    {
        [SerializeField] private readonly string id;
        [SerializeField] private readonly string type;

        /// <summary>
        /// The actual ID string value.
        /// </summary>
        public string Id => id;

        /// <summary>
        /// The type identifier for this reference.
        /// </summary>
        public string Type => type;

        /// <summary>
        /// Whether this reference ID is valid (has both ID and type).
        /// </summary>
        public bool IsValid => !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(type);

        /// <summary>
        /// Create a new ReferenceId with the specified ID and type.
        /// </summary>
        public ReferenceId(string id, string type)
        {
            if (string.IsNullOrEmpty(id))
                throw new ArgumentException("Reference ID cannot be null or empty", nameof(id));

            if (string.IsNullOrEmpty(type))
                throw new ArgumentException("Reference type cannot be null or empty", nameof(type));

            this.id = id;
            this.type = type;
        }

        /// <summary>
        /// Create a ReferenceId from an IReferenceable object.
        /// </summary>
        public static ReferenceId From(IReferenceable referenceable)
        {
            if (referenceable == null)
                throw new ArgumentNullException(nameof(referenceable));

            return new ReferenceId(referenceable.RefId, referenceable.RefType);
        }

        /// <summary>
        /// Try to create a ReferenceId from an IReferenceable object.
        /// Returns false if the object is null or has invalid reference data.
        /// </summary>
        public static bool TryFrom(IReferenceable referenceable, out ReferenceId referenceId)
        {
            if (referenceable == null ||
                string.IsNullOrEmpty(referenceable.RefId) ||
                string.IsNullOrEmpty(referenceable.RefType))
            {
                referenceId = default;
                return false;
            }

            referenceId = new ReferenceId(referenceable.RefId, referenceable.RefType);
            return true;
        }

        /// <summary>
        /// Get the string representation of this reference ID.
        /// </summary>
        public override string ToString() => IsValid ? $"{type}:{id}" : "InvalidReferenceId";

        /// <summary>
        /// Get a hash code for this reference ID.
        /// </summary>
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (id?.GetHashCode() ?? 0);
                hash = hash * 31 + (type?.GetHashCode() ?? 0);
                return hash;
            }
        }

        /// <summary>
        /// Check equality with another object.
        /// </summary>
        public override bool Equals(object obj) => obj is ReferenceId other && Equals(other);

        /// <summary>
        /// Check equality with another ReferenceId.
        /// </summary>
        public bool Equals(ReferenceId other) => id == other.id && type == other.type;

        /// <summary>
        /// Compare this ReferenceId with another for sorting purposes.
        /// </summary>
        public int CompareTo(ReferenceId other)
        {
            int typeComparison = string.Compare(type, other.type, StringComparison.Ordinal);
            if (typeComparison != 0) return typeComparison;

            return string.Compare(id, other.id, StringComparison.Ordinal);
        }

        /// <summary>
        /// Equality operator.
        /// </summary>
        public static bool operator ==(ReferenceId left, ReferenceId right) => left.Equals(right);

        /// <summary>
        /// Inequality operator.
        /// </summary>
        public static bool operator !=(ReferenceId left, ReferenceId right) => !left.Equals(right);

        /// <summary>
        /// Implicit conversion from ReferenceId to string (returns the full reference string).
        /// </summary>
        public static implicit operator string(ReferenceId referenceId) => referenceId.ToString();

        /// <summary>
        /// Try to parse a reference string back into a ReferenceId.
        /// Expected format: "Type:Id"
        /// </summary>
        public static bool TryParse(string referenceString, out ReferenceId referenceId)
        {
            if (string.IsNullOrEmpty(referenceString))
            {
                referenceId = default;
                return false;
            }

            int separatorIndex = referenceString.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= referenceString.Length - 1)
            {
                referenceId = default;
                return false;
            }

            string type = referenceString.Substring(0, separatorIndex);
            string id = referenceString.Substring(separatorIndex + 1);

            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(id))
            {
                referenceId = default;
                return false;
            }

            referenceId = new ReferenceId(id, type);
            return true;
        }

        /// <summary>
        /// An invalid/empty reference ID for use as a default value.
        /// </summary>
        public static readonly ReferenceId Invalid = new ReferenceId();
    }
}
