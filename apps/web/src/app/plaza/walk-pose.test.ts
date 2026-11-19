import * as THREE from "three";
import { describe, expect, it } from "vitest";
import { resetWalk, swingWalk, type WalkBone } from "./walk-pose";

const leg = (): WalkBone => {
  const bone = new THREE.Bone();
  bone.quaternion.setFromAxisAngle(new THREE.Vector3(0, 0, 1), 0.2);
  return { bone, axis: new THREE.Vector3(1, 0, 0), sign: 1, rest: bone.quaternion.clone() };
};

describe("walk pose", () => {
  it("swings from the rest pose each frame and does not add up (a bone without an idle track)", () => {
    const walk = [leg()];
    const tmp = new THREE.Quaternion();
    for (let frame = 0; frame < 120; frame++) {
      resetWalk(walk);
      swingWalk(walk, 0.5, tmp);
    }
    const expected = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(1, 0, 0), 0.5).multiply(walk[0]!.rest);
    expect(walk[0]!.bone.quaternion.angleTo(expected)).toBeLessThan(1e-6);
  });

  it("goes back to the rest pose when the walk stops", () => {
    const walk = [leg()];
    swingWalk(walk, 0.5, new THREE.Quaternion());
    resetWalk(walk);
    expect(walk[0]!.bone.quaternion.angleTo(walk[0]!.rest)).toBeLessThan(1e-6);
  });
});
