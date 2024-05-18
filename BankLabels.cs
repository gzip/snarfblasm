using System;
using System.Collections.Generic;
using System.Text;
using Romulus.Plugin;
using System.IO;

namespace snarfblasm
{
    class AddressLabels : IAddressLabels
    {
        BankLabels ramLabels = new BankLabels(-1);
        BankLabelList bankLabels = new BankLabelList();

        public IBankLabels Ram {
            get { return ramLabels; }
        }

        public IBankLabelList Banks {
            get { return bankLabels; }
        }
    }

    class BankLabelList : IBankLabelList
    {
        const int bankIndexLimit = 255;

        List<IBankLabels> banks = new List<IBankLabels>();

        public List<IBankLabels> GetBanks() {
            return banks;
        }

        public int Count {
            get { return GetBanks().Count; }
        }

        public IBankLabels this[int bankIndex] {
            get {
                if (bankIndex < 0 || bankIndex > bankIndexLimit)
                    throw new ArgumentException("Bank index is out of range.");

                for (int iBank = 0; iBank < banks.Count; iBank++) {
                    if (banks[iBank].BankIndex == bankIndex) {
                        return banks[iBank];
                    }
                }

                BankLabels newBank = new BankLabels(bankIndex);
                // Add the new bank object (in order)
                for (int iBank = 0; iBank < banks.Count; iBank++) {
                    // Insert before first bank that has higher index
                    if (banks[iBank].BankIndex > bankIndex) {
                        banks.Insert(iBank, newBank);
                        return newBank;
                    }
                }
                // If there is no bank with higher index, add the new bank to the end
                banks.Add(newBank);
                return newBank;
            }
        }
    }


    class BankLabels : IBankLabels
    {
        internal struct addressData
        {
            public string label;
            public string comment;
            public int size;
        }
        internal Dictionary<ushort, addressData> Labels = new Dictionary<ushort, addressData>();

        internal Dictionary<ushort, addressData> GetLabels() {
            return Labels;
        }

        public BankLabels(int bankIndex) {
            BankIndex = bankIndex;
        }

        public int BankIndex { get; private set; }

        public void AddComment(ushort address, string comment) {
            AddLabelData(address, null, 0, comment);
        }

        public void AddLabel(ushort address, string label) {
            AddLabelData(address, label, 0, null);
        }

        public void AddLabel(ushort address, string label, string comment) {
            AddLabelData(address, label, 0, comment);
        }

        public void AddArrayLabel(ushort address, string label, int byteCount) {
            AddLabelData(address, label, byteCount, null);
        }

        public void AddArrayLabel(ushort address, string label, int byteCount, string comment) {
            AddLabelData(address, label, byteCount, comment);
        }

        private void AddLabelData(ushort address, string label, int byteCount, string comment) {
            addressData data;

            // Get data for specified address, if present, and remove it from the dictionary so we can re-add updated data.
            if (Labels.TryGetValue(address, out data)) {
                Labels.Remove(address);
            } else {
                data = default(addressData);
            }

            if (label != null)
                data.label = label;
            if (comment != null)
                data.comment = comment;
            if (byteCount > 0)
                data.size = byteCount;

            Labels.Add(address, data);
        }

        /// <summary>
        /// Creates .mlb files for the Mesen 2 debugger
        /// </summary>
        /// <param name="bankLabels"></param>
        public byte[] BuildDebugFile(int bank) {
            MemoryStream outputStream = new MemoryStream();
            StreamWriter output = new StreamWriter(outputStream);

            string nlEntry = "";
            var labels = GetLabels();
            foreach (var entry in labels)
            {
                uint val = entry.Key;
                string name = entry.Value.label;
                string comment = entry.Value.comment;

                if (bank >= 0) {
                    val = (uint)((val >= 0xC000 ? val - 0x4000 : val) + (bank - 2) * 0x4000);
                    nlEntry = "NesPrgRom:" + val.ToString("X") + ":" + name;
                } else {
                    if (val < 0x2000) {
                        nlEntry = "NesInternalRam:" + val.ToString("X4") + ":" + name;
                    } else if (val >= 0x6000 && val < 0x8000) {
                        val -= 0x6000;
                        nlEntry = "NesSaveRam:" + val.ToString("X4") + ":" + name;
                    } else {
                        nlEntry = "NesMemory:" + val.ToString("X4") + ":" + name;
                    }
                }

                if (comment != null) {
                    nlEntry += ":" + comment;
                }

                output.WriteLine(nlEntry);
            }

            output.Flush();
            return outputStream.ToArray();
        }
    }
}
