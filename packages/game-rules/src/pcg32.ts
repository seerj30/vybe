/**
 * PCG32 (XSH-RR, 64-bit LCG state), the reference `pcg32_srandom_r` /
 * `pcg32_random_r` from pcg-random.org. Spec v1.3 §7.
 * BigInt keeps the 64-bit state exact; outputs are uint32 as `number`.
 */
const MASK64 = (1n << 64n) - 1n;
const MULTIPLIER = 6364136223846793005n;

export class Pcg32 {
  private state = 0n;
  private readonly inc: bigint;

  constructor(initState: bigint, initSeq: bigint) {
    this.inc = ((initSeq << 1n) | 1n) & MASK64;
    this.nextUint32();
    this.state = (this.state + initState) & MASK64;
    this.nextUint32();
  }

  /** Next output as an unsigned 32-bit integer. */
  nextUint32(): number {
    const old = this.state;
    this.state = (old * MULTIPLIER + this.inc) & MASK64;
    const xorShifted = Number((((old >> 18n) ^ old) >> 27n) & 0xffffffffn);
    const rot = Number(old >> 59n);
    return ((xorShifted >>> rot) | (xorShifted << ((32 - rot) & 31))) >>> 0;
  }

  /** Integer in [lo, hi] as `lo + (next() mod (hi - lo + 1))`; modulo bias accepted (§7). */
  nextInRange(lo: number, hi: number): number {
    if (!Number.isInteger(lo) || !Number.isInteger(hi) || hi < lo) {
      throw new RangeError(`invalid range [${lo}, ${hi}]`);
    }
    return lo + (this.nextUint32() % (hi - lo + 1));
  }
}
