import React, { useEffect, useMemo, useState } from "react";
import {
  cancelRender,
  continueRender,
  delayRender,
  staticFile,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";
import { useThree } from "@react-three/fiber";
import * as THREE from "three";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";

// Motion clips exported from Viggle (PINOC) as mixamo-glb, stored in public/viggle.
export type ViggleClip = "idle" | "point" | "walk" | "push" | "cheer" | "zombie";

// One segment of a character's timeline: play `clip` from frame `from`.
export type Beat = { clip: ViggleClip; from: number };

type Loaded = { scene: THREE.Group; clips: Map<ViggleClip, THREE.AnimationClip> };

const loader = new GLTFLoader();
const cache = new Map<ViggleClip, Promise<THREE.AnimationClip>>();

const loadGltf = (clip: ViggleClip) =>
  loader.loadAsync(staticFile(`viggle/${clip}.glb`));

// Viggle walk clips carry root motion; we move the character ourselves, so pin
// the hips horizontally (armature is Z-up: local x/y are the ground plane).
const pinRoot = (clip: THREE.AnimationClip) => {
  const track = clip.tracks.find((t) =>
    t.name.endsWith("mixamorigHips.position"),
  );
  if (track) {
    const v = track.values;
    const [x0, y0] = [v[0], v[1]];
    for (let i = 0; i < v.length; i += 3) {
      v[i] = x0;
      v[i + 1] = y0;
    }
  }
  return clip;
};

const loadClip = (clip: ViggleClip) => {
  if (!cache.has(clip)) {
    cache.set(
      clip,
      loadGltf(clip).then((g) => pinRoot(g.animations[0])),
    );
  }
  return cache.get(clip)!;
};

const useViggle = (
  meshFrom: ViggleClip,
  clips: ViggleClip[],
  colors: { body: string; joints: string },
) => {
  const [handle] = useState(() => delayRender(`Loading Viggle ${meshFrom}`));
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const key = clips.join(",");

  useEffect(() => {
    const names = key.split(",") as ViggleClip[];
    Promise.all([loadGltf(meshFrom), ...names.map(loadClip)])
      .then(([gltf, ...anims]) => {
        const scene = gltf.scene;
        scene.traverse((o) => {
          const m = o as THREE.SkinnedMesh;
          if (!m.isMesh) return;
          m.frustumCulled = false;
          const isJoint = m.name.toLowerCase().includes("joint");
          m.material = new THREE.MeshToonMaterial({
            side: THREE.DoubleSide,
            color: isJoint ? colors.joints : colors.body,
          });
        });
        // Normalize to 1.8 units tall, feet on y=0.
        const box = new THREE.Box3().setFromObject(scene);
        const s = 1.8 / (box.max.y - box.min.y);
        scene.scale.setScalar(s);
        scene.position.y = -box.min.y * s;
        setLoaded({
          scene,
          clips: new Map(names.map((n, i) => [n, anims[i]])),
        });
        // Let R3F draw the loaded model before the frame is captured.
        requestAnimationFrame(() =>
          requestAnimationFrame(() => setTimeout(() => continueRender(handle), 150)),
        );
      })
      .catch((err) => cancelRender(err));
  }, [handle, meshFrom, key, colors.body, colors.joints]);

  return loaded;
};

const BLEND = 8;
// Gestures hold their last pose instead of looping.
const LOOPING = new Set<ViggleClip>(["idle", "walk", "zombie"]);

export const ViggleCharacter: React.FC<{
  meshFrom: ViggleClip;
  beats: Beat[];
  body: string;
  joints: string;
  position?: (frame: number) => [number, number, number];
  rotationY?: number;
}> = ({ meshFrom, beats, body, joints, position, rotationY = 0 }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const clipNames = useMemo(
    () => Array.from(new Set(beats.map((b) => b.clip))),
    [beats],
  );
  const colors = useMemo(() => ({ body, joints }), [body, joints]);
  const loaded = useViggle(meshFrom, clipNames, colors);
  // The canvas runs frameloop="never"; draw once more after the model mounts.
  const advance = useThree((st) => st.advance);
  useEffect(() => {
    if (loaded) requestAnimationFrame(() => advance(performance.now()));
  }, [loaded, advance]);

  const rig = useMemo(() => {
    if (!loaded) return null;
    const mixer = new THREE.AnimationMixer(loaded.scene);
    const actions = new Map(
      clipNames.map((n) => {
        const a = mixer.clipAction(loaded.clips.get(n)!);
        a.play();
        return [n, a] as const;
      }),
    );
    return { mixer, actions };
  }, [loaded, clipNames]);

  if (!loaded || !rig) return null;

  // Drive the mixer from the current frame so every render is deterministic.
  const idx = beats.reduce((acc, b, i) => (frame >= b.from ? i : acc), 0);
  const weights = new Map<ViggleClip, number>(clipNames.map((n) => [n, 0]));
  const cur = beats[idx];
  const prev = beats[idx - 1];
  const mix = prev ? Math.min(1, (frame - cur.from) / BLEND) : 1;
  weights.set(cur.clip, (weights.get(cur.clip) ?? 0) + mix);
  if (prev && mix < 1) {
    weights.set(prev.clip, (weights.get(prev.clip) ?? 0) + 1 - mix);
  }
  rig.actions.forEach((action, name) => {
    const beat =
      name === cur.clip ? cur : prev && name === prev.clip ? prev : cur;
    const t = Math.max(0, frame - beat.from) / fps;
    const d = action.getClip().duration;
    action.time = LOOPING.has(name) ? t % d : Math.min(t, d - 0.001);
    action.setEffectiveWeight(weights.get(name) ?? 0);
  });
  rig.mixer.update(0);

  const pos = position ? position(frame) : ([0, 0, 0] as const);
  return (
    <group position={pos as [number, number, number]} rotation={[0, rotationY, 0]}>
      <primitive object={loaded.scene} />
    </group>
  );
};

export const Lights: React.FC = () => (
  <>
    <ambientLight intensity={1.4} />
    <directionalLight position={[2, 4, 5]} intensity={2.2} />
  </>
);
