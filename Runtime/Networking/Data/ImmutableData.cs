using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Linq;
using UnityEngine;

public struct ImmutableData
{
    public string modelId;
    public readonly IReadOnlyDictionary<string, object> data;

    public readonly DateTime createdAt;
    public bool IsValid => modelId != null && data != null;
    
    /// <summary>
    /// Gets the approximate memory size of this ImmutableData object in bytes
    /// This includes the size of the data dictionary and its contents
    /// </summary>
    public int GetSize()
    {
        int totalSize = 0;
        
        // Base struct size (string references + DateTime)
        totalSize += IntPtr.Size * 2; // Two string references
        totalSize += 8; // DateTime is 8 bytes
        
        // ModelId string size
        if (!string.IsNullOrEmpty(modelId))
        {
            totalSize += modelId.Length * 2; // UTF-16 characters are 2 bytes each
        }
        
        // Data dictionary size
        if (data != null)
        {
            // Dictionary overhead
            totalSize += IntPtr.Size * 2; // Dictionary internal structure
            
            // Each key-value pair
            foreach (var kvp in data)
            {
                // Key size (string)
                if (!string.IsNullOrEmpty(kvp.Key))
                {
                    totalSize += kvp.Key.Length * 2;
                }
                
                // Value size
                totalSize += GetValueSize(kvp.Value);
            }
        }
        
        return totalSize;
    }
    
    /// <summary>
    /// Helper method to calculate the size of a value object
    /// </summary>
    private int GetValueSize(object value)
    {
        if (value == null) return IntPtr.Size; // Null reference
        
        var valueType = value.GetType();
        
        // Primitive types
        if (valueType == typeof(string))
        {
            var str = (string)value;
            return string.IsNullOrEmpty(str) ? IntPtr.Size : str.Length * 2;
        }
        else if (valueType == typeof(int)) return 4;
        else if (valueType == typeof(float)) return 4;
        else if (valueType == typeof(bool)) return 1;
        else if (valueType == typeof(double)) return 8;
        else if (valueType == typeof(long)) return 8;
        else if (valueType == typeof(short)) return 2;
        else if (valueType == typeof(byte)) return 1;
        
        // Collections
        else if (value is System.Collections.IList list)
        {
            int size = IntPtr.Size * 2; // List overhead
            foreach (var item in list)
            {
                size += GetValueSize(item);
            }
            return size;
        }
        else if (value is System.Collections.IDictionary dict)
        {
            int size = IntPtr.Size * 2; // Dictionary overhead
            foreach (System.Collections.DictionaryEntry entry in dict)
            {
                size += GetValueSize(entry.Key);
                size += GetValueSize(entry.Value);
            }
            return size;
        }
        
        // ImmutableData objects
        else if (value is ImmutableData immutableData)
        {
            return immutableData.GetSize();
        }
        
        // Arrays
        else if (valueType.IsArray)
        {
            var array = (Array)value;
            int size = IntPtr.Size * 2; // Array overhead
            var elementType = valueType.GetElementType();
            
            if (elementType.IsPrimitive)
            {
                size += array.Length * Marshal.SizeOf(elementType);
            }
            else
            {
                foreach (var item in array)
                {
                    size += GetValueSize(item);
                }
            }
            return size;
        }
        
        // Default: estimate based on type
        return IntPtr.Size;
    }
    
    /// <summary>
    /// Gets the number of data fields in this object
    /// </summary>
    public int GetFieldCount() => data?.Count ?? 0;
    
    /// <summary>
    /// Gets the number of items if this represents an array, otherwise returns 1
    /// </summary>
    public int GetItemCount()
    {
        if (data == null) return 0;
        
        int totalItems = 0;
        foreach (var kvp in data)
        {
            if (kvp.Value is System.Collections.IList list)
            {
                totalItems += list.Count;
            }
            else
            {
                totalItems += 1;
            }
        }
        
        return totalItems;
    }

    public string[] GetKeys() => data.Keys.ToArray();

    public static ImmutableData Unknown => new ImmutableData("unknown", new Dictionary<string, object>());

    public ImmutableData(string modelId, Dictionary<string, object> data)
    {
        this.modelId = modelId;
        this.data = data;
        this.createdAt = DateTime.UtcNow;
    }

    public T Get<T>(string key)
    {
        if (data.TryGetValue(key, out object value))
        {
            try
            {
                return CastValue<T>(value, key);
            }
            catch (System.Exception ex)
            {
                throw new InvalidCastException($"Failed to cast value for key '{key}' from {value?.GetType().Name} to {typeof(T).Name}: {ex.Message}", ex);
            }
        }
        else
        {
            throw new KeyNotFoundException($"Key {key} not found in data");
        }
    }

    public bool TryGet<T>(string key, out T value)
    {
        if (data.TryGetValue(key, out object result))
        {
            try
            {
                value = CastValue<T>(result, key);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to cast value for key '{key}' from {result?.GetType().Name} to {typeof(T).Name}: {ex.Message}");
                value = default(T);
                return false;
            }
        }
        
        value = default(T);
        return false;
    }

    /// <summary>
    /// Helper method to cast values to the target type, handling arrays and collections
    /// </summary>
    private T CastValue<T>(object value, string key)
    {
        // Handle special cases for arrays and collections
        if (typeof(T) == typeof(List<ImmutableData>) && value is List<ImmutableData>)
        {
            return (T)(object)value;
        }
        else if (typeof(T) == typeof(ImmutableData[]) && value is List<ImmutableData>)
        {
            var arrayResult = ((List<ImmutableData>)value).ToArray();
            return (T)(object)arrayResult;
        }
        else if (typeof(T) == typeof(ImmutableData[]) && value is ImmutableData[])
        {
            return (T)(object)value;
        }
        else if (typeof(T).IsArray && value is System.Collections.IList)
        {
            // Handle generic array types
            var list = (System.Collections.IList)value;
            var elementType = typeof(T).GetElementType();
            var array = Array.CreateInstance(elementType, list.Count);
            list.CopyTo(array, 0);
            return (T)(object)array;
        }
        else if (typeof(T).IsGenericType && typeof(T).GetGenericTypeDefinition() == typeof(List<>) && value is System.Collections.IList)
        {
            // Handle generic List types
            var list = (System.Collections.IList)value;
            var elementType = typeof(T).GetGenericArguments()[0];
            var genericListType = typeof(List<>).MakeGenericType(elementType);
            var newList = (System.Collections.IList)Activator.CreateInstance(genericListType);
            
            foreach (var item in list)
            {
                if (item.GetType() == elementType || elementType.IsAssignableFrom(item.GetType()))
                {
                    newList.Add(item);
                }
            }
            
            return (T)newList;
        }
        else if (value is T)
        {
            // Direct type match
            return (T)value;
        }
        else if (typeof(T).IsAssignableFrom(value.GetType()))
        {
            // Type is assignable
            return (T)value;
        }
        else
        {
            // Try explicit cast as last resort
            return (T)Convert.ChangeType(value, typeof(T));
        }
    }

    /// <summary>
    /// Gets an array field as a list of ImmutableData objects
    /// </summary>
    /// <param name="key">The field key</param>
    /// <returns>List of ImmutableData objects, or empty list if not found or invalid</returns>
    public List<ImmutableData> GetArray(string key)
    {
        if (TryGetArray(key, out var result))
        {
            return result;
        }
        return new List<ImmutableData>();
    }

    /// <summary>
    /// Tries to get an array field as a list of ImmutableData objects
    /// </summary>
    /// <param name="key">The field key</param>
    /// <param name="result">The resulting list of ImmutableData objects</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool TryGetArray(string key, out List<ImmutableData> result)
    {
        if (data.TryGetValue(key, out object value))
        {
            if (value is List<ImmutableData> arrayData)
            {
                result = arrayData;
                return true;
            }
            else if (value is ImmutableData[] arrayDataArray)
            {
                result = new List<ImmutableData>(arrayDataArray);
                return true;
            }
        }
        
        result = new List<ImmutableData>();
        return false;
    }

    /// <summary>
    /// Gets an array field as a typed array
    /// </summary>
    /// <typeparam name="T">The type to convert array elements to</typeparam>
    /// <param name="key">The field key</param>
    /// <returns>Array of the specified type, or empty array if not found or invalid</returns>
    public T[] GetTypedArray<T>(string key)
    {
        if (data.TryGetValue(key, out object value))
        {
            if (value is List<T> listData)
            {
                return listData.ToArray();
            }
            else if (value is T[] arrayData)
            {
                return arrayData;
            }
        }
        
        return new T[0];
    }

    /// <summary>
    /// Gets an array field as an array of ImmutableData objects
    /// This is the most convenient way to access array fields that contain models
    /// </summary>
    /// <param name="key">The field key</param>
    /// <returns>Array of ImmutableData objects, or empty array if not found or invalid</returns>
    public ImmutableData[] GetImmutableDataArray(string key)
    {
        if (data.TryGetValue(key, out object value))
        {
            if (value is List<ImmutableData> listData)
            {
                return listData.ToArray();
            }
            else if (value is ImmutableData[] arrayData)
            {
                return arrayData;
            }
            else if (value is System.Collections.IList list)
            {
                // Convert any IList to ImmutableData array
                var result = new ImmutableData[list.Count];
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is ImmutableData immutableData)
                    {
                        result[i] = immutableData;
                    }
                }
                return result;
            }
        }
        
        return new ImmutableData[0];
    }

    /// <summary>
    /// Tries to get an array field as an array of ImmutableData objects
    /// </summary>
    /// <param name="key">The field key</param>
    /// <param name="result">The resulting array of ImmutableData objects</param>
    /// <returns>True if successful, false otherwise</returns>
    public bool TryGetImmutableDataArray(string key, out ImmutableData[] result)
    {
        if (data.TryGetValue(key, out object value))
        {
            if (value is List<ImmutableData> listData)
            {
                result = listData.ToArray();
                return true;
            }
            else if (value is ImmutableData[] arrayData)
            {
                result = arrayData;
                return true;
            }
            else if (value is System.Collections.IList list)
            {
                // Convert any IList to ImmutableData array
                var tempResult = new ImmutableData[list.Count];
                bool allValid = true;
                
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is ImmutableData immutableData)
                    {
                        tempResult[i] = immutableData;
                    }
                    else
                    {
                        allValid = false;
                        break;
                    }
                }
                
                if (allValid)
                {
                    result = tempResult;
                    return true;
                }
            }
        }
        
        result = new ImmutableData[0];
        return false;
    }

    public bool ContainsKey(string key)
    {
        return data.ContainsKey(key);
    }

    /// <summary>
    /// Debug method to inspect what's stored for a specific key
    /// </summary>
    /// <param name="key">The field key to inspect</param>
    /// <returns>Debug information about the stored value</returns>
    public string DebugFieldInfo(string key)
    {
        if (data.TryGetValue(key, out object value))
        {
            if (value == null)
                return $"Key '{key}': null";
            
            var typeName = value.GetType().Name;
            var isArray = value.GetType().IsArray;
            var isList = value is System.Collections.IList;
            
            if (isArray)
            {
                var array = (Array)value;
                return $"Key '{key}': {typeName} with {array.Length} elements. Element type: {array.GetType().GetElementType()?.Name}";
            }
            else if (isList)
            {
                var list = (System.Collections.IList)value;
                var elementType = list.Count > 0 ? list[0]?.GetType().Name : "unknown";
                return $"Key '{key}': {typeName} with {list.Count} elements. Element type: {elementType}";
            }
            else
            {
                return $"Key '{key}': {typeName} = {value}";
            }
        }
        
        return $"Key '{key}': not found";
    }

    /// <summary>
    /// Debug method to inspect all fields in this data object
    /// </summary>
    /// <returns>Debug information about all stored values</returns>
    public string DebugAllFields()
    {
        var info = new System.Text.StringBuilder();
        info.AppendLine($"ImmutableData for model: {modelId}");
        info.AppendLine($"Total fields: {data.Count}");
        
        foreach (var kvp in data)
        {
            info.AppendLine(DebugFieldInfo(kvp.Key));
        }
        
        return info.ToString();
    }
}