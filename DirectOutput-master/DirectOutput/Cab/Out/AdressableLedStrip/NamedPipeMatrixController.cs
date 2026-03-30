using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;

namespace DirectOutput.Cab.Out.AdressableLedStrip
{
    /// <summary>
    /// Output controller which streams full RGB frame data to a local named pipe.
    /// Intended as a virtual matrix transport that avoids virtual COM drivers.
    /// </summary>
    public class NamedPipeMatrixController : OutputControllerCompleteBase
    {
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
                Pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.WriteThrough);
                Pipe.Connect(ConnectTimeoutMs);
                Sequence = 0;
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

            // Frame message format (little-endian):
            // [0..3]   Magic "VDMF"
            // [4]      Version (1)
            // [5..8]   Int32 sequence
            // [9..12]  Int32 payload length
            // [13..]   RGB payload bytes
            int payloadLength = OutputValues != null ? OutputValues.Length : 0;
            byte[] frame = new byte[13 + payloadLength];
            frame[0] = (byte)'V';
            frame[1] = (byte)'D';
            frame[2] = (byte)'M';
            frame[3] = (byte)'F';
            frame[4] = 1;
            Buffer.BlockCopy(BitConverter.GetBytes(Sequence++), 0, frame, 5, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(payloadLength), 0, frame, 9, 4);
            if (payloadLength > 0)
            {
                Buffer.BlockCopy(OutputValues, 0, frame, 13, payloadLength);
            }

            Pipe.Write(frame, 0, frame.Length);
            Pipe.Flush();

            if (FrameThrottleMs > 0)
            {
                System.Threading.Thread.Sleep(FrameThrottleMs);
            }
        }

        public NamedPipeMatrixController()
        {
            NumberOfLedsPerStrip[0] = 256;
        }
    }
}
