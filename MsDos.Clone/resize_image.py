"""
Resize a FAT16 hard disk image to 10,485,760 bytes (10 MB) while preserving all files.

Reads the existing MBR+FAT16 image, extracts every file and subdirectory,
then creates a fresh 10 MB image with a new FAT16 partition and writes them back.
"""

import struct, os, sys, shutil
from collections import namedtuple

# ---------- geometry for the new 10 MB image ----------
TARGET_SIZE   = 10_485_760          # exactly 10 MB
SECTOR        = 512
HEADS         = 4
SEC_PER_TRACK = 16
CYLINDERS     = TARGET_SIZE // (HEADS * SEC_PER_TRACK * SECTOR)  # 320
PART_START    = SEC_PER_TRACK       # first partition starts at head 1, cyl 0 = LBA 16
TOTAL_SECTORS = TARGET_SIZE // SECTOR  # 20480
PART_SECTORS  = TOTAL_SECTORS - PART_START  # 20464

# FAT16 BPB parameters for the new partition
BPS           = 512
SPC           = 4                   # sectors per cluster (2048 B)
RESERVED      = 1
NUM_FATS      = 2
ROOT_ENTRIES  = 512
ROOT_DIR_SECS = (ROOT_ENTRIES * 32 + BPS - 1) // BPS  # 32
SPF           = 20                  # sectors per FAT — pre-calculated to fit
DATA_START    = RESERVED + NUM_FATS * SPF + ROOT_DIR_SECS  # 73
DATA_SECTORS  = PART_SECTORS - DATA_START  # 20391
TOTAL_CLUSTERS = DATA_SECTORS // SPC  # 5097
CLUSTER_BYTES = SPC * BPS           # 2048

assert 4086 <= TOTAL_CLUSTERS <= 65525, "Not valid FAT16 cluster count!"

DirEntry = namedtuple("DirEntry", "name attr cluster size raw")

# ====================================================================
# Reading helpers (source image)
# ====================================================================

def read_fat16_partition(img_data, part_offset):
    """Return (bpb_dict, fat_array, root_entries_raw, data_region_bytes)."""
    bpb = img_data[part_offset : part_offset + 512]
    bps   = struct.unpack_from('<H', bpb, 11)[0]
    spc   = bpb[13]
    res   = struct.unpack_from('<H', bpb, 14)[0]
    nfat  = bpb[16]
    rents = struct.unpack_from('<H', bpb, 17)[0]
    t16   = struct.unpack_from('<H', bpb, 19)[0]
    spf   = struct.unpack_from('<H', bpb, 22)[0]
    t32   = struct.unpack_from('<I', bpb, 32)[0]
    total = t16 if t16 else t32

    root_dir_secs = (rents * 32 + bps - 1) // bps
    data_start_sec = res + nfat * spf + root_dir_secs
    data_clusters  = (total - data_start_sec) // spc

    fat_off = part_offset + res * bps
    fat_raw = img_data[fat_off : fat_off + spf * bps]
    fat = [struct.unpack_from('<H', fat_raw, i * 2)[0] for i in range(data_clusters + 2)]

    root_off = part_offset + (res + nfat * spf) * bps
    root_raw = img_data[root_off : root_off + root_dir_secs * bps]

    data_off = part_offset + data_start_sec * bps

    return {
        'bps': bps, 'spc': spc, 'res': res, 'nfat': nfat,
        'root_entries': rents, 'spf': spf, 'total': total,
        'data_start_sec': data_start_sec, 'data_clusters': data_clusters,
        'part_offset': part_offset,
    }, fat, root_raw, data_off


def follow_chain(fat, start_cluster):
    """Return list of cluster numbers for a FAT chain."""
    clusters = []
    c = start_cluster
    while 2 <= c <= 0xFFEF:
        clusters.append(c)
        c = fat[c]
    return clusters


def read_cluster_data(img_data, data_off, bpb, cluster_no):
    """Read one cluster from the data region."""
    off = data_off + (cluster_no - 2) * bpb['spc'] * bpb['bps']
    size = bpb['spc'] * bpb['bps']
    return img_data[off : off + size]


def parse_dir_entries(raw):
    """Parse 32-byte directory entries from raw bytes. Yields DirEntry."""
    for i in range(0, len(raw), 32):
        entry = raw[i:i+32]
        if entry[0] == 0x00:
            break                       # end of directory
        if entry[0] == 0xE5:
            continue                    # deleted
        attr = entry[11]
        if attr == 0x0F:
            continue                    # LFN entry — skip
        # 8.3 name
        name = entry[0:11]
        cluster = struct.unpack_from('<H', entry, 26)[0]
        size    = struct.unpack_from('<I', entry, 28)[0]
        yield DirEntry(name, attr, cluster, size, entry)


def extract_tree(img_data, data_off, bpb, fat, dir_raw):
    """
    Recursively extract directory tree.
    Returns a list of (rel_path, attr, data_bytes, raw_entry) tuples.
    Directories are represented with data_bytes=None.
    """
    results = []
    for ent in parse_dir_entries(dir_raw):
        short = ent.name
        basename = short[0:8].rstrip(b'\x20').decode('ascii', errors='replace')
        ext      = short[8:11].rstrip(b'\x20').decode('ascii', errors='replace')
        if ext:
            fname = f"{basename}.{ext}"
        else:
            fname = basename

        if basename in ('.', '..'):
            continue

        if ent.attr & 0x08:
            # Volume label — preserve as-is
            results.append((fname, ent.attr, None, ent.raw))
            continue

        is_dir = bool(ent.attr & 0x10)

        if is_dir:
            # Read subdirectory clusters
            chain = follow_chain(fat, ent.cluster)
            sub_raw = b''.join(read_cluster_data(img_data, data_off, bpb, c) for c in chain)
            results.append((fname, ent.attr, None, ent.raw))
            sub_items = extract_tree(img_data, data_off, bpb, fat, sub_raw)
            for sub_name, sa, sd, sr in sub_items:
                results.append((f"{fname}/{sub_name}", sa, sd, sr))
        else:
            # Regular file
            if ent.cluster >= 2 and ent.size > 0:
                chain = follow_chain(fat, ent.cluster)
                file_data = b''.join(read_cluster_data(img_data, data_off, bpb, c) for c in chain)
                file_data = file_data[:ent.size]
            else:
                file_data = b''
            results.append((fname, ent.attr, file_data, ent.raw))

    return results


# ====================================================================
# Writing helpers (destination image)
# ====================================================================

def lba_to_chs(lba, heads, spt):
    """Convert LBA to (C, H, S) tuple. S is 1-based."""
    c = lba // (heads * spt)
    rem = lba % (heads * spt)
    h = rem // spt
    s = rem % spt + 1
    return (c, h, s)


def pack_chs(c, h, s):
    """Pack CHS into 3 bytes for MBR partition entry."""
    # byte 0 = head, byte 1 = sector (bits 0-5) | cyl high (bits 6-7), byte 2 = cyl low
    return bytes([h & 0xFF, ((c >> 2) & 0xC0) | (s & 0x3F), c & 0xFF])


def build_mbr(part_type, part_start_lba, part_sectors, heads, spt):
    """Build a 512-byte MBR with one active partition."""
    mbr = bytearray(512)
    # Boot signature
    mbr[510] = 0x55
    mbr[511] = 0xAA

    # Partition entry 1 at offset 446
    pe = 446
    mbr[pe] = 0x80  # active

    start_chs = lba_to_chs(part_start_lba, heads, spt)
    end_lba   = part_start_lba + part_sectors - 1
    end_chs   = lba_to_chs(end_lba, heads, spt)

    mbr[pe+1:pe+4] = pack_chs(*start_chs)
    mbr[pe+4]      = part_type
    mbr[pe+5:pe+8] = pack_chs(*end_chs)
    struct.pack_into('<I', mbr, pe + 8, part_start_lba)
    struct.pack_into('<I', mbr, pe + 12, part_sectors)

    return bytes(mbr)


def build_boot_sector(part_sectors, spc, reserved, num_fats, root_entries, spf,
                      spt, heads, hidden, vol_label=b'DOS400     ', oem=b'MSDOS4.0'):
    """Build a FAT16 boot sector (512 bytes)."""
    bs = bytearray(512)
    bs[0] = 0xEB; bs[1] = 0x3C; bs[2] = 0x90  # JMP short + NOP
    bs[3:11] = oem.ljust(8, b'\x20')[:8]

    struct.pack_into('<H', bs, 11, BPS)
    bs[13] = spc
    struct.pack_into('<H', bs, 14, reserved)
    bs[16] = num_fats
    struct.pack_into('<H', bs, 17, root_entries)

    if part_sectors <= 0xFFFF:
        struct.pack_into('<H', bs, 19, part_sectors)
        struct.pack_into('<I', bs, 32, 0)
    else:
        struct.pack_into('<H', bs, 19, 0)
        struct.pack_into('<I', bs, 32, part_sectors)

    bs[21] = 0xF8  # hard disk
    struct.pack_into('<H', bs, 22, spf)
    struct.pack_into('<H', bs, 24, spt)
    struct.pack_into('<H', bs, 26, heads)
    struct.pack_into('<I', bs, 28, hidden)

    # Extended BPB (FAT16)
    bs[36] = 0x80  # drive number
    bs[37] = 0x00  # reserved
    bs[38] = 0x29  # extended boot sig
    struct.pack_into('<I', bs, 39, 0x12345678)  # volume serial
    bs[43:54] = vol_label.ljust(11, b'\x20')[:11]
    bs[54:62] = b'FAT16   '

    # Boot signature
    bs[510] = 0x55
    bs[511] = 0xAA
    return bytes(bs)


class FAT16Writer:
    """Manages writing files into a new FAT16 image buffer."""

    def __init__(self, image, part_offset, part_sectors, spc, reserved,
                 num_fats, root_entries, spf):
        self.image = image
        self.part_offset = part_offset
        self.spc = spc
        self.bps = BPS
        self.cluster_size = spc * BPS
        self.reserved = reserved
        self.num_fats = num_fats
        self.root_entries = root_entries
        self.spf = spf

        self.root_dir_secs = (root_entries * 32 + BPS - 1) // BPS
        self.data_start_sec = reserved + num_fats * spf + self.root_dir_secs
        self.data_sectors = part_sectors - self.data_start_sec
        self.total_clusters = self.data_sectors // spc

        # FAT table in memory (array of 16-bit values)
        self.fat = [0] * (self.total_clusters + 2)
        self.fat[0] = 0xFFF8  # media descriptor
        self.fat[1] = 0xFFFF

        self.next_free_cluster = 2

        # Root directory buffer
        self.root_dir = bytearray(self.root_dir_secs * BPS)
        self.root_dir_pos = 0

    def _alloc_cluster(self):
        if self.next_free_cluster >= self.total_clusters + 2:
            raise RuntimeError("Out of clusters!")
        c = self.next_free_cluster
        self.next_free_cluster += 1
        return c

    def _write_cluster(self, cluster_no, data):
        off = self.part_offset + self.data_start_sec * self.bps + \
              (cluster_no - 2) * self.cluster_size
        self.image[off : off + len(data)] = data

    def write_file_data(self, data):
        """Write file data, allocating clusters. Returns first cluster (or 0 if empty)."""
        if not data:
            return 0

        clusters = []
        offset = 0
        while offset < len(data):
            chunk = data[offset : offset + self.cluster_size]
            # Pad to full cluster
            if len(chunk) < self.cluster_size:
                chunk = chunk + b'\x00' * (self.cluster_size - len(chunk))
            c = self._alloc_cluster()
            clusters.append(c)
            self._write_cluster(c, chunk)
            offset += self.cluster_size

        # Build chain
        for i in range(len(clusters) - 1):
            self.fat[clusters[i]] = clusters[i + 1]
        self.fat[clusters[-1]] = 0xFFFF  # end of chain

        return clusters[0]

    def write_dir_data(self, entries_raw):
        """Write a subdirectory's entry data to clusters. Returns first cluster."""
        return self.write_file_data(entries_raw)

    def add_root_entry(self, entry_raw_32):
        """Add a 32-byte entry to the root directory."""
        if self.root_dir_pos + 32 > len(self.root_dir):
            raise RuntimeError("Root directory full!")
        self.root_dir[self.root_dir_pos : self.root_dir_pos + 32] = entry_raw_32
        self.root_dir_pos += 32

    def flush(self):
        """Write FAT tables and root directory into the image buffer."""
        # FAT
        fat_bytes = bytearray(self.spf * self.bps)
        for i, val in enumerate(self.fat):
            struct.pack_into('<H', fat_bytes, i * 2, val)

        for f_idx in range(self.num_fats):
            off = self.part_offset + (self.reserved + f_idx * self.spf) * self.bps
            self.image[off : off + len(fat_bytes)] = fat_bytes

        # Root directory
        off = self.part_offset + (self.reserved + self.num_fats * self.spf) * self.bps
        self.image[off : off + len(self.root_dir)] = self.root_dir


def make_dir_entry(raw_template, new_cluster, new_size=None):
    """Clone a 32-byte dir entry but update the starting cluster and optionally size."""
    entry = bytearray(raw_template)
    struct.pack_into('<H', entry, 26, new_cluster)
    if new_size is not None:
        struct.pack_into('<I', entry, 28, new_size)
    return bytes(entry)


def write_tree(writer, items, prefix=""):
    """
    Write files from extracted tree into the FAT16 writer.
    `items` is the list from extract_tree. We process items at the current
    directory level and recurse for subdirectories.
    """
    # Group items by top-level component
    # Items at this level have no '/' in their path (after removing prefix)
    current_level = []
    subdirs = {}  # dirname -> list of (subpath, attr, data, raw)

    for path, attr, data, raw in items:
        if '/' in path:
            top, rest = path.split('/', 1)
            subdirs.setdefault(top, []).append((rest, attr, data, raw))
        else:
            current_level.append((path, attr, data, raw))

    entries_32 = []

    for path, attr, data, raw in current_level:
        if attr & 0x08:
            # Volume label
            entries_32.append(raw)
            continue

        is_dir = bool(attr & 0x10)

        if is_dir:
            # Build subdirectory
            sub_items = subdirs.get(path, [])
            sub_entries = build_subdir_entries(writer, sub_items, raw)
            cluster = writer.write_dir_data(sub_entries)
            # Patch the . entry's cluster to point to itself
            # and write it
            entry = make_dir_entry(raw, cluster)
            entries_32.append(entry)

            # Now fix the . entry inside the subdirectory data
            # The . entry is at offset 0, .. at offset 32
            off_in_data = writer.part_offset + writer.data_start_sec * writer.bps + \
                          (cluster - 2) * writer.cluster_size
            struct.pack_into('<H', writer.image, off_in_data + 26, cluster)
            # .. entry cluster = 0 for root
            struct.pack_into('<H', writer.image, off_in_data + 32 + 26, 0)
        else:
            # Regular file
            if data:
                cluster = writer.write_file_data(data)
            else:
                cluster = 0
            entry = make_dir_entry(raw, cluster, len(data) if data else 0)
            entries_32.append(entry)

    return entries_32


def build_subdir_entries(writer, sub_items, dir_raw_entry):
    """Build raw bytes for a subdirectory (. and .. plus contents)."""
    # Create . entry
    dot = bytearray(32)
    dot[0:11] = b'.          '
    dot[11] = 0x10
    # Copy time/date from original
    dot[22:26] = dir_raw_entry[22:26]

    # Create .. entry
    dotdot = bytearray(32)
    dotdot[0:11] = b'..         '
    dotdot[11] = 0x10
    dotdot[22:26] = dir_raw_entry[22:26]
    # .. cluster will be fixed up by caller (0 for root)

    child_entries = write_tree(writer, sub_items)

    all_entries = bytes(dot) + bytes(dotdot)
    for e in child_entries:
        all_entries += e

    return all_entries


# ====================================================================
# Main
# ====================================================================

def main():
    src_path = r'c:\Users\clefw\source\repos\MS-DOS\MsDos.Clone\47Mb Hard Disk.img'
    dst_path = r'c:\Users\clefw\source\repos\MS-DOS\MsDos.Clone\47Mb Hard Disk.img.new'

    print(f"Reading source image: {src_path}")
    with open(src_path, 'rb') as f:
        img = f.read()
    print(f"  Source size: {len(img)} bytes ({len(img)/1024/1024:.2f} MB)")

    # Parse MBR
    pe = img[446:462]
    part_type  = pe[4]
    lba_start  = struct.unpack_from('<I', pe, 8)[0]
    num_sec    = struct.unpack_from('<I', pe, 12)[0]
    part_off   = lba_start * 512
    print(f"  Partition: type=0x{part_type:02X}, start_LBA={lba_start}, sectors={num_sec}")

    # Parse FAT16
    bpb, fat, root_raw, data_off = read_fat16_partition(img, part_off)
    print(f"  FAT16: clusters={bpb['data_clusters']}, spc={bpb['spc']}, spf={bpb['spf']}")

    # Extract files
    print("Extracting file tree...")
    tree = extract_tree(img, data_off, bpb, fat, root_raw)
    total_file_size = sum(len(d) for _, _, d, _ in tree if d is not None)
    print(f"  Found {len(tree)} entries, total file data: {total_file_size} bytes")
    for path, attr, data, _ in tree:
        kind = "DIR" if (attr & 0x10) else ("VOL" if (attr & 0x08) else "FILE")
        sz = len(data) if data else 0
        print(f"    [{kind}] {path} ({sz} bytes)")

    # Build new image
    print(f"\nBuilding new {TARGET_SIZE} byte image...")
    new_img = bytearray(TARGET_SIZE)

    # MBR
    mbr = build_mbr(0x04, PART_START, PART_SECTORS, HEADS, SEC_PER_TRACK)
    # Use type 0x04 (FAT16 < 32MB) since partition is < 32MB
    new_img[0:512] = mbr

    # Get volume label from tree (if any)
    vol_label = b'DOS400     '
    for path, attr, data, raw in tree:
        if attr & 0x08:
            vol_label = raw[0:11]
            break

    # Boot sector
    bs = build_boot_sector(PART_SECTORS, SPC, RESERVED, NUM_FATS, ROOT_ENTRIES,
                           SPF, SEC_PER_TRACK, HEADS, PART_START, vol_label)
    bs_off = PART_START * SECTOR
    new_img[bs_off : bs_off + 512] = bs

    # FAT16 writer
    writer = FAT16Writer(new_img, bs_off, PART_SECTORS, SPC, RESERVED,
                         NUM_FATS, ROOT_ENTRIES, SPF)

    # Write file tree to root directory
    root_entries_32 = write_tree(writer, tree)
    for entry in root_entries_32:
        writer.add_root_entry(entry)

    writer.flush()

    # Write output
    with open(dst_path, 'wb') as f:
        f.write(new_img)
    print(f"\nWrote new image: {dst_path}")
    print(f"  Size: {len(new_img)} bytes ({len(new_img)/1024/1024:.2f} MB)")

    # Replace original
    bak_path = src_path + '.bak'
    print(f"\nBacking up original to: {bak_path}")
    shutil.copy2(src_path, bak_path)
    shutil.move(dst_path, src_path)
    print(f"Replaced original with resized image.")
    print("Done!")


if __name__ == '__main__':
    main()
