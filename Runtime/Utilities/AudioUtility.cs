using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Utils
{
    public static class AudioUtility
    {
        public static byte[] GetByteArrayFromAudioClip(AudioClip clip)
        {
            if (clip == null)
            {
                throw new ArgumentNullException(nameof(clip)); // Handle null input
            }

            float[] audioData = new float[clip.samples * clip.channels];
            clip.GetData(audioData, 0);

            // Calculate byte array size based on data format (assuming 4 bytes per float)
            int byteArraySize = audioData.Length * sizeof(float);

            // Convert float array to byte array
            byte[] byteArray = new byte[byteArraySize];
            Buffer.BlockCopy(audioData, 0, byteArray, 0, byteArraySize);

            return byteArray;
        }

        public static AudioClip CreateAudioClipFromByteArray(byte[] byteArray)
        {
            if (byteArray == null || byteArray.Length == 0)
            {
                throw new ArgumentException("Invalid byte array input"); // Handle invalid input
            }

            // Try common PCM audio formats (adjust based on your knowledge of potential formats)
            int[] possibleFrequencies = { 44100, 48000 };
            int[] possibleChannels = { 1, 2 };

            foreach (int frequency in possibleFrequencies)
            {
                foreach (int channels in possibleChannels)
                {
                    try
                    {
                        int audioLength = byteArray.Length / sizeof(float);
                        float[] audioData = new float[audioLength];
                        Buffer.BlockCopy(byteArray, 0, audioData, 0, byteArray.Length);

                        AudioClip clip = AudioClip.Create("ConvertedClip", audioLength, channels, frequency, false);
                        clip.SetData(audioData, 0);
                        return clip; // Success! Return the created clip
                    }
                    catch (UnityException)
                    {
                        // Handle creation exception (might indicate format mismatch)
                    }
                }
            }

            Debug.LogError("Failed to create AudioClip from byte array with unknown format");
            return null;
        }
    }
}