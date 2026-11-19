import * as THREE from "three";

/** A bone that swings while the avatar walks, around the avatar's x axis (in the bone's parent space). */
export interface WalkBone {
  bone: THREE.Bone;
  axis: THREE.Vector3;
  sign: number;
  /** The bind rotation. The idle clip does not animate every walk bone, so the swing needs a fixed base. */
  rest: THREE.Quaternion;
}

/**
 * Puts the walk bones back to their rest rotation. Call it before the mixer update of each frame: a clip that
 * animates a bone then writes over it, and a bone without a track starts from the rest pose. Without this, the swing
 * of every frame adds to the one before and the legs turn round the hip.
 */
export function resetWalk(walk: readonly WalkBone[]): void {
  for (const { bone, rest } of walk) bone.quaternion.copy(rest);
}

/** Adds this frame's swing (radians, before the sign of each bone) on top of the pose after the mixer update. */
export function swingWalk(walk: readonly WalkBone[], swing: number, tmp: THREE.Quaternion): void {
  for (const { bone, axis, sign } of walk) {
    tmp.setFromAxisAngle(axis, swing * sign);
    bone.quaternion.premultiply(tmp);
  }
}
