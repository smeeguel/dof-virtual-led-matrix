using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using DirectOutput.Cab.Overrides;

namespace DirectOutput.Cab.Out.AdressableLedStrip
{
    /// <summary>
    /// Virtual LED strip output controller for software-only matrix viewers.
    /// Branch task note: this class was added to provide a non-COM transport path
    /// (local named pipe) for virtual matrix rendering.
    /// </summary>
    public class VirtualLEDStripController : OutputControllerCompleteBase
    {
        // Branch task note: Keep strip-count style properties (matching Teensy-like
        // cabinet configs) so users can switch controller tags with minimal edits.
        protected int[] NumberOfLedsPerStrip = new int[10];

        public int NumberOfLedsStrip1 { get { return NumberOfLedsPerStrip[0]; } set { NumberOfLedsPerStrip[0] = value; SetupOutputs(); } }
        public int NumberOfLedsStrip2 { get { return NumberOfLedsPerStrip[1]; } set { NumberOfLedsPerStrip[1] = value; SetupOutputs(); } }
        public int NumberOfLedsStrip3 { get { return NumberOfLedsPerStrip[2]; } set { NumberOfLedsPerStrip[2] = value; SetupOutputs(); } }
        public int NumberOfLedsStrip4 { get { return NumberOfLedsPerStrip[3]; } set { NumberOfLedsPerStrip[3] = value; SetupOutputs(); } }
        public int NumberOfLedsStrip5 { get { return NumberOfLedsPerStrip[4]; } set { NumberOfLedsPerStrip[4] = value; SetupOutputs(); } }
        public int NumberOfLedsStrip6 { get { return NumberOfLedsPerStrip[5]; } set { NumberOfLedsPerStrip[5] = value; SetupOutputs(); } }
        public int NumberOfLedsStrip7 { get { return NumberOfLedsPerStrip[6]; } set { NumberOfLedsPerStrip[6] = value; SetupOutputs(); } }
        public int NumberOfLedsStrip8 { get { return NumberOfLedsPerStrip[7]; } set { NumberOfLedsPerStrip[7] = value; SetupOutputs(); } }
        public int NumberOfLedsStrip9 { get { return NumberOfLedsPerStrip[8]; } set { NumberOfLedsPerStrip[8] = value; SetupOutputs(); } }
        public int NumberOfLedsStrip10 { get { return NumberOfLedsPerStrip[9]; } set { NumberOfLedsPerStrip[9] = value; SetupOutputs(); } }

        private string _PipeName = "VirtualDofMatrix";
        public string PipeName
        {
            get { return _PipeName; }
            set { _PipeName = value; }
        }

        private int _ConnectTimeoutMs = 2000;
        public int ConnectTimeoutMs
        {
            get { return _ConnectTimeoutMs; }
            set { _ConnectTimeoutMs = value.IsBetween(100, 30000) ? value : 2000; }
        }

        private int _ReconnectDelayMs = 250;
        public int ReconnectDelayMs
        {
            get { return _ReconnectDelayMs; }
            set { _ReconnectDelayMs = value.IsBetween(10, 5000) ? value : 250; }
        }

        private int _FrameThrottleMs = 0;
        public int FrameThrottleMs
        {
            get { return _FrameThrottleMs; }
            set { _FrameThrottleMs = value.IsBetween(0, 500) ? value : 0; }
        }

        private NamedPipeClientStream Pipe;
        private int Sequence = 0;
        private string LastPublishedTableName = string.Empty;
        private string LastPublishedRomName = string.Empty;

        protected override int GetNumberOfConfiguredOutputs()
        {
            return NumberOfLedsPerStrip.Sum() * 3;
        }

        protected override bool VerifySettings()
        {
            if (PipeName.IsNullOrWhiteSpace())
            {
                Log.Warning("The PipeName has not been specified for {0} \"{1}\".".Build(this.GetType().Name, Name));
                return false;
            }

            if (NumberOfLedsPerStrip.All(n => n <= 0))
            {
                Log.Warning("At least one strip must have a positive LED count for {0} \"{1}\".".Build(this.GetType().Name, Name));
                return false;
            }

            if (NumberOfLedsPerStrip.Any(n => n < 0))
            {
                Log.Warning("Strip LED counts must be zero or positive for {0} \"{1}\".".Build(this.GetType().Name, Name));
                return false;
            }

            return true;
        }

        protected override void ConnectToController()
        {
            DisconnectFromController();

            try
            {
                // Branch task note: DOF side acts as named-pipe client; the viewer app
                // opens a NamedPipeServerStream and waits for this connection.
                Pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.WriteThrough);
                Pipe.Connect(ConnectTimeoutMs);
                Sequence = 0;
                LastPublishedTableName = string.Empty;
                LastPublishedRomName = string.Empty;
                Log.Write("{0} \"{1}\" connected to named pipe \"{2}\".".Build(this.GetType().Name, Name, PipeName));
            }
            catch (Exception ex)
            {
                throw new Exception("Could not connect {0} \"{1}\" to named pipe \"{2}\".".Build(this.GetType().Name, Name, PipeName), ex);
            }
        }

        protected override void DisconnectFromController()
        {
            if (Pipe != null)
            {
                TryPublishTableContextMetadata(string.Empty, string.Empty);

                try
                {
                    Pipe.Flush();
                }
                catch { }

                try
                {
                    Pipe.Dispose();
                }
                catch { }

                Pipe = null;
            }

            if (ReconnectDelayMs > 0)
            {
                System.Threading.Thread.Sleep(ReconnectDelayMs);
            }
        }

        protected override void UpdateOutputs(byte[] OutputValues)
        {
            if (Pipe == null || !Pipe.IsConnected)
            {
                throw new IOException("Named pipe is not connected.");
            }

            PublishTableContextIfChanged();

            // Branch task note: lightweight binary frame envelope (little-endian):
            // [0..3]   Magic "VDMF"
            // [4]      Message discriminator: 1=RGB frame, 2=table context metadata
            // [5..8]   Int32 sequence
            // [9..12]  Int32 payload length
            // [13..]   RGB payload bytes
            int payloadLength = OutputValues != null ? OutputValues.Length : 0;
            WriteMessage(1, OutputValues ?? new byte[0], payloadLength);

            if (FrameThrottleMs > 0)
            {
                System.Threading.Thread.Sleep(FrameThrottleMs);
            }
        }

        private void PublishTableContextIfChanged()
        {
            var tableName = TableOverrideSettings.Instance.activetableName ?? string.Empty;
            var romName = TableOverrideSettings.Instance.activeromName ?? string.Empty;

            if (tableName.Equals(LastPublishedTableName, StringComparison.OrdinalIgnoreCase)
                && romName.Equals(LastPublishedRomName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            TryPublishTableContextMetadata(tableName, romName);
        }

        private void TryPublishTableContextMetadata(string tableName, string romName)
        {
            if (Pipe == null || !Pipe.IsConnected)
            {
                return;
            }

            var payloadText = "{0}\t{1}\t".Build(tableName ?? string.Empty, romName ?? string.Empty);
            var payloadBytes = Encoding.UTF8.GetBytes(payloadText);
            WriteMessage(2, payloadBytes, payloadBytes.Length);
            LastPublishedTableName = tableName ?? string.Empty;
            LastPublishedRomName = romName ?? string.Empty;
        }

        private void WriteMessage(byte messageType, byte[] payloadBytes, int payloadLength)
        {
            byte[] frame = new byte[13 + payloadLength];
            frame[0] = (byte)'V';
            frame[1] = (byte)'D';
            frame[2] = (byte)'M';
            frame[3] = (byte)'F';
            frame[4] = messageType;
            Buffer.BlockCopy(BitConverter.GetBytes(Sequence++), 0, frame, 5, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(payloadLength), 0, frame, 9, 4);
            if (payloadLength > 0)
            {
                Buffer.BlockCopy(payloadBytes, 0, frame, 13, payloadLength);
            }

            Pipe.Write(frame, 0, frame.Length);
            Pipe.Flush();
        }

        public VirtualLEDStripController()
        {
            NumberOfLedsPerStrip[0] = 256;
        }
    }

    /// <summary>
    /// Backward-compatible alias for earlier branch revisions.
    /// Branch task note: allows old Cabinet.xml tags (<NamedPipeMatrixController>)
    /// to continue working while the preferred user-facing name is
    /// <VirtualLEDStripController>.
    /// </summary>
    public class NamedPipeMatrixController : VirtualLEDStripController
    {
    }
}
