#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace Conduit
{

    sealed class FfmpegEncoderSpec
    {
        public FfmpegEncoderSpec(
            string encoderName,
            string displayName,
            string inputFilter,
            string[] preInputArguments,
            string[] codecArguments,
            bool isGif)
        {
            EncoderName = encoderName;
            DisplayName = displayName;
            InputFilter = inputFilter;
            PreInputArguments = preInputArguments;
            CodecArguments = codecArguments;
            IsGif = isGif;
            ProbeFilter = inputFilter.Replace("vflip,", string.Empty);
            // quality values are validated before selection and do not change encoder availability.
            ProbeKey = encoderName
                       + "\n" + inputFilter
                       + "\n" + string.Join("\n", preInputArguments);
        }

        public string EncoderName { get; }
        public string DisplayName { get; }
        public string InputFilter { get; }
        public string ProbeFilter { get; }
        public string[] PreInputArguments { get; }
        public string[] CodecArguments { get; }
        public bool IsGif { get; }
        public string ProbeKey { get; }
    }
}
