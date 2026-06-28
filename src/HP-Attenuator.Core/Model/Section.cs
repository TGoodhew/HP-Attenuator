namespace HpAttenuator.Model
{
    /// <summary>
    /// A single switchable attenuator section behind one 11713A relay/pushbutton.
    /// </summary>
    public sealed class Section
    {
        /// <summary>The 11713A front-panel digit / relay number (1-4 = ATTEN X, 5-8 = ATTEN Y).</summary>
        public int Digit { get; }

        /// <summary>Attenuation contributed by this section, in dB, when engaged (A).</summary>
        public int Decibels { get; }

        public Section(int digit, int decibels)
        {
            Digit = digit;
            Decibels = decibels;
        }
    }
}
