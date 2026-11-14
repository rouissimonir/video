import React, { useEffect, useRef, useState } from "react";
import {
  cancelRender,
  continueRender,
  delayRender,
  staticFile,
  useCurrentFrame,
} from "remotion";
import { Quat } from "playcanvas";
import { Animation, Character, Scene } from "@viggle/splat-engine";

// Motion clips from Viggle (splat-skeleton-glb), stored in public/splat.
export type SplatClip = "idle" | "point" | "walk" | "push" | "cheer" | "zombie";

// Play `clip` from scene frame `from` until the next beat starts.
export type Beat = { clip: SplatClip; from: number };

export type Actor = {
  // .vsplat character file in public/splat
  file: "survivor" | "zombie";
  beats: Beat[];
  position: (frame: number) => [number, number, number];
  rotationY?: (frame: number) => number;
};

const CLIPS: SplatClip[] = ["idle", "point", "walk", "push", "cheer", "zombie"];
const LOOPING = new Set<SplatClip>(["idle", "walk", "zombie"]);
const CLIP_FPS = 30;

// Vertical field of view in degrees, and the camera distance. The caller maps
// pixels to world units with `pxPerUnit`.
export const FOV = 30;
export const CAM_Z = 10;
export const pxPerUnit = (heightPx: number) =>
  heightPx / (2 * CAM_Z * Math.tan((FOV * Math.PI) / 360));

const frames = (n: number) => new Promise<void>((r) => {
  const step = () => (n-- <= 0 ? r() : requestAnimationFrame(step));
  step();
});

export const SplatStage: React.FC<{
  width: number;
  height: number;
  floorPx: number;
  actors: Actor[];
}> = ({ width, height, floorPx, actors }) => {
  const frame = useCurrentFrame();
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const [ready, setReady] = useState<{
    scene: Scene;
    chars: Character[];
  } | null>(null);
  const [initHandle] = useState(() => delayRender("Loading splat characters"));
  const actorsRef = useRef(actors);
  actorsRef.current = actors;

  useEffect(() => {
    let cancelled = false;
    let scene: Scene | null = null;
    (async () => {
      const camY = (floorPx - height / 2) / pxPerUnit(height);
      scene = await Scene.create(canvasRef.current!, {
        fov: FOV,
        bgColor: { r: 0, g: 0, b: 0, a: 0 },
        preserveDrawingBuffer: true,
        cameraPosition: { x: 0, y: camY, z: CAM_Z },
        cameraTarget: { x: 0, y: camY, z: 0 },
      });
      await Promise.all(
        CLIPS.map((c) =>
          Animation.loadGlb(scene!, staticFile(`splat/${c}.glb`), c),
        ),
      );
      const chars = await Promise.all(
        actorsRef.current.map(async (a) => {
          const c = await Character.load(
            scene!,
            staticFile(`splat/${a.file}.vsplat`),
            { name: a.file },
          );
          c.detachAutoUpdate();
          return c;
        }),
      );
      if (cancelled) return;
      scene.start();
      setReady({ scene, chars });
      continueRender(initHandle);
    })().catch((e) => cancelRender(e));
    return () => {
      cancelled = true;
      scene?.destroy();
    };
  }, [floorPx, height, initHandle]);

  // Pose every character for this exact frame, then let the GPU draw it.
  useEffect(() => {
    if (!ready) return;
    const handle = delayRender(`Posing splat characters, frame ${frame}`);
    const { scene, chars } = ready;
    actorsRef.current.forEach((a, i) => {
      const c = chars[i];
      const beat = a.beats.reduce((acc, b) => (frame >= b.from ? b : acc), a.beats[0]);
      const loop = LOOPING.has(beat.clip);
      const anim = scene.skeletalAnimationLibrary.get(beat.clip)!;
      const t = Math.max(0, frame - beat.from) * (CLIP_FPS / 30);
      c.playAnimation(beat.clip, { loop, rootMotion: false });
      c.animationTime = loop ? t % anim.numFrames : Math.min(t, anim.numFrames - 1);
      c.armature.update(0);
      const [x, y, z] = a.position(frame);
      c.setPosition(x, y, z);
      const rot = new Quat().setFromEulerAngles(0, ((a.rotationY?.(frame) ?? 0) * 180) / Math.PI, 0);
      c.armature.move(undefined, rot);
      c.requestSort();
    });
    frames(4)
      .then(() => new Promise((r) => setTimeout(r, 250)))
      .then(() => frames(2))
      .then(() => continueRender(handle))
      .catch((e) => cancelRender(e));
  }, [ready, frame]);

  return (
    <canvas
      ref={canvasRef}
      width={width}
      height={height}
      style={{ position: "absolute", inset: 0, width, height }}
    />
  );
};
