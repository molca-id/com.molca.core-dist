using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Networking.Data
{
    /// <summary>
    /// Represents a single field mapping between source and target data structures.
    /// </summary>
    [Serializable]
    public class MappingField
    {
        public string from;
        public string to;
        public DataMapping nestedMapping;

        public MappingField() { from = string.Empty; to = string.Empty; nestedMapping = null; }
        public MappingField(string from, string to) { this.from = from; this.to = to; this.nestedMapping = null; }
        public MappingField(string from, string to, DataMapping nestedMapping) { this.from = from; this.to = to; this.nestedMapping = nestedMapping; }
    }
}
