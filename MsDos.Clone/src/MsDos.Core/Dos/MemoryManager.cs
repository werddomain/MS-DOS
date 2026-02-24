using MsDos.Core.Memory;

namespace MsDos.Core.Dos;

/// <summary>
/// DOS Memory Control Block (MCB) chain manager.
/// Manages conventional memory allocation using the linked-list MCB structure.
/// 
/// MCB format (16 bytes at segment boundary):
///   Byte 0: Type — 'M' (0x4D) = not last, 'Z' (0x5A) = last block
///   Bytes 1-2: Owner PSP segment (0 = free)
///   Bytes 3-4: Size in paragraphs (excluding the MCB itself)
///   Bytes 5-7: Reserved
///   Bytes 8-15: Owner name (DOS 4.0+)
/// </summary>
public sealed class MemoryManager
{
    private readonly MemoryBus _mem;
    private ushort _firstMcb; // Segment of the first MCB

    /// <summary>Allocation strategy: 0=First Fit, 1=Best Fit, 2=Last Fit.</summary>
    public byte AllocationStrategy { get; set; }

    public MemoryManager(MemoryBus memory)
    {
        _mem = memory;
    }

    /// <summary>
    /// Initialize the MCB chain. Call after memory is cleared.
    /// Sets up a single free block covering all conventional memory from startSegment to endSegment.
    /// </summary>
    /// <param name="startSegment">First MCB segment (typically 0x0600 or after DOS kernel).</param>
    /// <param name="endSegment">Last usable paragraph (typically 0x9FFF for 640K).</param>
    public void Initialize(ushort startSegment, ushort endSegment)
    {
        _firstMcb = startSegment;
        ushort size = (ushort)(endSegment - startSegment - 1); // -1 for the MCB itself

        WriteMcb(startSegment, 'Z', 0x0000, size, "");
    }

    /// <summary>First MCB segment.</summary>
    public ushort FirstMcb => _firstMcb;

    /// <summary>
    /// Set up the MCB chain for a COM/EXE file loaded at a fixed segment.
    /// Creates a system block covering the area before the load segment,
    /// then assigns all remaining conventional memory to the program.
    /// Must be called after <see cref="Initialize"/> and before the program runs.
    /// </summary>
    /// <param name="loadSegment">The segment where the program PSP is loaded (e.g., 0x1000).</param>
    /// <param name="programName">Optional 8-char name for the MCB (e.g., "COMMAND").</param>
    public void SetupForLoadedProgram(ushort loadSegment, string programName = "")
    {
        // System block: covers _firstMcb+1 through loadSegment-2
        // so the next MCB lands exactly at loadSegment-1.
        ushort systemSize = (ushort)(loadSegment - _firstMcb - 2);

        // Program block: MCB at loadSegment-1, data from loadSegment to end of chain.
        // Read the current total size from the initial free block to compute the remainder.
        ushort totalSize = ReadMcbSize(_firstMcb); // size of the single free 'Z' block
        ushort programSize = (ushort)(totalSize - systemSize - 1); // -1 for the program MCB itself

        // Write system MCB at _firstMcb
        WriteMcb(_firstMcb, 'M', 0x0008, systemSize, "SC");

        // Write program MCB at loadSegment - 1
        ushort programMcb = (ushort)(loadSegment - 1);
        WriteMcb(programMcb, 'Z', loadSegment, programSize, programName);
    }

    /// <summary>
    /// Allocate a block of memory.
    /// INT 21h/48h: BX = paragraphs requested.
    /// Returns the usable data segment (MCB + 1), or 0 on failure.
    /// On failure, sets maxAvailable to the largest free block size.
    /// </summary>
    public ushort Allocate(ushort paragraphs, ushort ownerPsp, out ushort maxAvailable)
    {
        maxAvailable = 0;
        ushort bestSeg = 0;
        ushort bestSize = 0xFFFF;
        ushort lastFreeSeg = 0;
        ushort lastFreeSize = 0;

        // Walk the MCB chain
        ushort seg = _firstMcb;
        while (true)
        {
            byte type = ReadMcbType(seg);
            ushort owner = ReadMcbOwner(seg);
            ushort size = ReadMcbSize(seg);

            if (owner == 0) // Free block
            {
                // Coalesce adjacent free blocks
                while (type == 'M')
                {
                    ushort nextSeg = (ushort)(seg + size + 1);
                    ushort nextOwner = ReadMcbOwner(nextSeg);
                    if (nextOwner == 0) // Next is also free
                    {
                        byte nextType = ReadMcbType(nextSeg);
                        ushort nextSize = ReadMcbSize(nextSeg);
                        size = (ushort)(size + nextSize + 1);
                        type = nextType;
                        WriteMcb(seg, (char)type, 0, size, "");
                    }
                    else break;
                }

                if (size > maxAvailable)
                    maxAvailable = size;

                if (size >= paragraphs)
                {
                    switch (AllocationStrategy)
                    {
                        case 0: // First Fit
                            if (bestSeg == 0)
                            {
                                bestSeg = seg;
                                bestSize = size;
                            }
                            break;
                        case 1: // Best Fit
                            if (size < bestSize)
                            {
                                bestSeg = seg;
                                bestSize = size;
                            }
                            break;
                        case 2: // Last Fit
                            lastFreeSeg = seg;
                            lastFreeSize = size;
                            break;
                    }
                }
            }

            if (type == 'Z') break;
            seg = (ushort)(seg + size + 1);
        }

        if (AllocationStrategy == 2 && lastFreeSeg != 0)
        {
            bestSeg = lastFreeSeg;
            bestSize = lastFreeSize;
        }

        if (bestSeg == 0) return 0; // No suitable block found

        // Split the block if there's enough room for another MCB
        if (bestSize > paragraphs + 1)
        {
            char origType = (char)ReadMcbType(bestSeg);
            ushort remaining = (ushort)(bestSize - paragraphs - 1);
            ushort newFreeSeg = (ushort)(bestSeg + paragraphs + 1);

            WriteMcb(bestSeg, 'M', ownerPsp, paragraphs, "");
            WriteMcb(newFreeSeg, origType, 0, remaining, "");
        }
        else
        {
            // Use the entire block
            WriteMcbOwner(bestSeg, ownerPsp);
        }

        return (ushort)(bestSeg + 1); // Return data segment (past the MCB)
    }

    /// <summary>
    /// Free a previously allocated block.
    /// INT 21h/49h: ES = segment to free (data segment, not MCB).
    /// Returns true on success, false if invalid segment.
    /// </summary>
    public bool Free(ushort dataSegment)
    {
        ushort mcbSeg = (ushort)(dataSegment - 1);

        // Validate MCB
        byte type = ReadMcbType(mcbSeg);
        if (type != 'M' && type != 'Z') return false;

        WriteMcbOwner(mcbSeg, 0); // Mark as free
        WriteMcbName(mcbSeg, "");
        return true;
    }

    /// <summary>
    /// Resize (reallocate) a memory block.
    /// INT 21h/4Ah: ES = data segment, BX = new size in paragraphs.
    /// Returns true on success. On failure, sets maxAvailable.
    /// </summary>
    public bool Resize(ushort dataSegment, ushort newParagraphs, out ushort maxAvailable)
    {
        maxAvailable = 0;
        ushort mcbSeg = (ushort)(dataSegment - 1);

        byte type = ReadMcbType(mcbSeg);
        ushort currentSize = ReadMcbSize(mcbSeg);

        if (type != 'M' && type != 'Z') return false;

        if (newParagraphs == currentSize)
            return true;

        if (newParagraphs < currentSize)
        {
            // Shrink: create a new free block after
            if (currentSize - newParagraphs > 1)
            {
                ushort newFreeSeg = (ushort)(mcbSeg + newParagraphs + 1);
                ushort freeSize = (ushort)(currentSize - newParagraphs - 1);
                WriteMcb(mcbSeg, 'M', ReadMcbOwner(mcbSeg), newParagraphs, ReadMcbName(mcbSeg));
                WriteMcb(newFreeSeg, (char)type, 0, freeSize, "");
            }
            else
            {
                WriteMcbSize(mcbSeg, newParagraphs);
            }
            return true;
        }

        // Growing: try to absorb following free blocks
        ushort totalAvail = currentSize;
        if (type == 'M')
        {
            ushort nextSeg = (ushort)(mcbSeg + currentSize + 1);
            while (true)
            {
                byte nextType = ReadMcbType(nextSeg);
                if (ReadMcbOwner(nextSeg) != 0) break; // Not free
                totalAvail += (ushort)(ReadMcbSize(nextSeg) + 1);
                if (nextType == 'Z')
                {
                    type = (byte)'Z'; // This becomes the last block
                    break;
                }
                nextSeg = (ushort)(nextSeg + ReadMcbSize(nextSeg) + 1);
            }
        }

        maxAvailable = totalAvail;

        if (totalAvail < newParagraphs)
            return false;

        // Use the merged space
        if (totalAvail > newParagraphs + 1)
        {
            ushort newFreeSeg = (ushort)(mcbSeg + newParagraphs + 1);
            ushort freeSize = (ushort)(totalAvail - newParagraphs - 1);
            WriteMcb(mcbSeg, 'M', ReadMcbOwner(mcbSeg), newParagraphs, ReadMcbName(mcbSeg));
            WriteMcb(newFreeSeg, (char)type, 0, freeSize, "");
        }
        else
        {
            WriteMcb(mcbSeg, (char)type, ReadMcbOwner(mcbSeg), totalAvail, ReadMcbName(mcbSeg));
        }
        return true;
    }

    /// <summary>Free all memory blocks owned by a specific PSP.</summary>
    public void FreeAll(ushort ownerPsp)
    {
        ushort seg = _firstMcb;
        while (true)
        {
            byte type = ReadMcbType(seg);
            if (ReadMcbOwner(seg) == ownerPsp)
            {
                WriteMcbOwner(seg, 0);
                WriteMcbName(seg, "");
            }
            if (type == 'Z') break;
            seg = (ushort)(seg + ReadMcbSize(seg) + 1);
        }
    }

    // --- MCB field accessors ---

    private byte ReadMcbType(ushort seg) => _mem.ReadByte(seg, 0);
    private ushort ReadMcbOwner(ushort seg) => _mem.ReadWord(seg, 1);
    private ushort ReadMcbSize(ushort seg) => _mem.ReadWord(seg, 3);

    private void WriteMcbOwner(ushort seg, ushort owner) => _mem.WriteWord(seg, 1, owner);
    private void WriteMcbSize(ushort seg, ushort size) => _mem.WriteWord(seg, 3, size);

    private string ReadMcbName(ushort seg)
    {
        char[] name = new char[8];
        for (int i = 0; i < 8; i++)
        {
            byte b = _mem.ReadByte(seg, (ushort)(8 + i));
            if (b == 0) break;
            name[i] = (char)b;
        }
        return new string(name).TrimEnd('\0');
    }

    private void WriteMcbName(ushort seg, string name)
    {
        for (int i = 0; i < 8; i++)
        {
            byte b = i < name.Length ? (byte)name[i] : (byte)0;
            _mem.WriteByte(seg, (ushort)(8 + i), b);
        }
    }

    private void WriteMcb(ushort seg, char type, ushort owner, ushort size, string name)
    {
        _mem.WriteByte(seg, 0, (byte)type);
        _mem.WriteWord(seg, 1, owner);
        _mem.WriteWord(seg, 3, size);
        // Reserved bytes 5-7
        _mem.WriteByte(seg, 5, 0);
        _mem.WriteByte(seg, 6, 0);
        _mem.WriteByte(seg, 7, 0);
        WriteMcbName(seg, name);
    }
}
