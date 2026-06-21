using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Utils
{
    [UnityEngine.Icon("Packages/com.molca.core/Editor/Icons/molca-utils.png")]
    [CreateAssetMenu(fileName = "New SharedString", menuName = "Molca/Utils/Shared String", order = 80)]
    public class SharedString : ScriptableObject
    {
        public string key;
        public string value;

        public static implicit operator string(SharedString sharedString) => sharedString.value;
    }
}