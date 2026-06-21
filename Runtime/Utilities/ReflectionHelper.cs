using System.Reflection;

public static class ReflectionHelper
{
    public static PropertyInfo[] GetAllProperties(object obj)
    {
        return obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
    }
}