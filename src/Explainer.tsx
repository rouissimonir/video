import React from "react";
import {
  AbsoluteFill,
  Easing,
  interpolate,
  spring,
  useCurrentFrame,
  useVideoConfig,
} from "remotion";

const FONT = "'Arial Black', Impact, sans-serif";
const BG = "#0E1117";
const ORANGE = "#E8825A";
const MUTED = "#8B93A7";

const START = 90; // first step begins
const STEP = 120; // frames per step
const ROW0 = 490;
const PITCH = 232;
const ICON_X = 170;

type Step = {
  label: string;
  caption: string;
  color: string;
  icon: (f: number) => React.ReactNode;
};

const stroke = {
  stroke: "#fff",
  strokeWidth: 7,
  strokeLinecap: "round" as const,
  strokeLinejoin: "round" as const,
  fill: "none",
};

const STEPS: Step[] = [
  {
    label: "YOU TYPE A PROMPT",
    caption: "“fix the login bug”",
    color: "#3B82F6",
    icon: () => (
      <g {...stroke}>
        <rect x={-34} y={0} width={68} height={30} rx={6} />
        <path d="M-20 12 H-12 M-2 12 H6 M16 12 H20" strokeWidth={5} />
        <circle cx={0} cy={-26} r={11} />
        <path d="M0 -15 V0" />
      </g>
    ),
  },
  {
    label: "IT GOES TO THE MODEL",
    caption: "prompt + context, sent over the API",
    color: "#8B5CF6",
    icon: (f) => (
      <g {...stroke}>
        <path d="M-30 14 a16 16 0 0 1 4 -31 a22 22 0 0 1 42 4 a14 14 0 0 1 -2 27 Z" />
        <path
          d={`M0 ${-2 + Math.sin(f / 5) * 3} V-14 M-8 -8 L0 -16 L8 -8`}
          strokeWidth={6}
        />
      </g>
    ),
  },
  {
    label: "MODEL PICKS A TOOL",
    caption: "Read · Edit · Bash · Search",
    color: ORANGE,
    icon: (f) => (
      <g {...stroke} transform={`rotate(${f * 2})`}>
        <circle r={14} />
        {[0, 45, 90, 135, 180, 225, 270, 315].map((d) => (
          <path key={d} d="M0 -22 V-32" transform={`rotate(${d})`} strokeWidth={9} />
        ))}
      </g>
    ),
  },
  {
    label: "RUNS ON YOUR COMPUTER",
    caption: "Claude Code executes it locally",
    color: "#10B981",
    icon: (f) => (
      <g {...stroke}>
        <rect x={-34} y={-28} width={68} height={46} rx={6} />
        <path d="M-44 28 H44" />
        <path d="M-20 -14 L-8 -5 L-20 4" strokeWidth={6} />
        <path
          d="M2 5 H18"
          strokeWidth={6}
          opacity={f % 30 < 15 ? 1 : 0.2}
        />
      </g>
    ),
  },
  {
    label: "RESULT COMES BACK",
    caption: "output is fed back to the model",
    color: "#F59E0B",
    icon: (f) => (
      <g {...stroke}>
        <path d="M-26 -30 H14 L28 -16 V30 H-26 Z" />
        <path d="M-14 -4 H14 M-14 10 H8" strokeWidth={5} />
        <path
          d={`M${30 - Math.sin(f / 5) * 4} 36 H-34 M-24 28 L-34 36 L-24 44`}
          strokeWidth={5}
          opacity={0}
        />
      </g>
    ),
  },
  {
    label: "YOU GET THE ANSWER",
    caption: "streamed right into your terminal",
    color: "#EC4899",
    icon: () => (
      <g {...stroke}>
        <path d="M-34 -28 H34 V16 H4 L-12 32 V16 H-34 Z" />
        <path d="M-14 -5 L-4 5 L16 -14" strokeWidth={8} />
      </g>
    ),
  },
];

const Row: React.FC<{ step: Step; index: number }> = ({ step, index }) => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const start = START + index * STEP;
  const p = spring({
    frame: frame - start,
    fps,
    config: { damping: 14, stiffness: 120 },
  });
  const active = frame >= start && frame < start + STEP;
  const done = frame >= start + STEP;
  const y = ROW0 + index * PITCH;
  const opacity = frame < start ? 0 : done ? 0.78 : 1;
  const local = frame - start;
  const typed =
    index === 0
      ? step.caption.slice(
          0,
          Math.max(0, Math.floor(interpolate(local, [15, 70], [0, step.caption.length], { extrapolateLeft: "clamp", extrapolateRight: "clamp" }))),
        )
      : step.caption;
  const pulse = active ? 1 + Math.sin(local / 5) * 0.04 : 1;

  return (
    <div
      style={{
        position: "absolute",
        left: 0,
        top: y - 100,
        width: 1080,
        height: 200,
        opacity: opacity * Math.min(1, p * 2),
        transform: `translateX(${interpolate(p, [0, 1], [120, 0])}px)`,
      }}
    >
      <svg
        width={1080}
        height={200}
        style={{ position: "absolute", left: 0, top: 0, overflow: "visible" }}
      >
        <g transform={`translate(${ICON_X} 100) scale(${pulse})`}>
          <circle
            r={84}
            fill={step.color}
            stroke={active ? "#fff" : "none"}
            strokeWidth={6}
          />
          {step.icon(frame)}
        </g>
        <g transform={`translate(${ICON_X + 62} ${100 - 62})`}>
          <circle r={26} fill="#fff" />
          <text
            textAnchor="middle"
            y={11}
            fontFamily={FONT}
            fontSize={32}
            fill={BG}
          >
            {index + 1}
          </text>
        </g>
      </svg>
      <div style={{ position: "absolute", left: 310, top: 38, right: 50 }}>
        <div
          style={{
            fontFamily: FONT,
            fontSize: 43,
            lineHeight: 1.05,
            color: "#fff",
          }}
        >
          {step.label}
        </div>
        <div
          style={{
            fontFamily: "Arial, sans-serif",
            fontWeight: 700,
            fontSize: 38,
            marginTop: 14,
            color: active ? step.color : MUTED,
          }}
        >
          {typed}
          {index === 0 && active && local % 20 < 10 ? "▌" : ""}
        </div>
      </div>
    </div>
  );
};

const Rail: React.FC = () => {
  const frame = useCurrentFrame();
  const segs = STEPS.slice(1).map((_, i) => {
    const t0 = START + (i + 1) * STEP;
    const prog = interpolate(frame, [t0, t0 + 30], [0, 1], {
      extrapolateLeft: "clamp",
      extrapolateRight: "clamp",
      easing: Easing.inOut(Easing.cubic),
    });
    const yA = ROW0 + i * PITCH + 90;
    const yB = ROW0 + (i + 1) * PITCH - 90;
    return { prog, yA, yB, color: STEPS[i + 1].color };
  });
  return (
    <svg
      width={1080}
      height={1920}
      style={{ position: "absolute", inset: 0 }}
    >
      {segs.map((s, i) => {
        const endY = s.yA + (s.yB - s.yA) * s.prog;
        return (
          <g key={i}>
            <path
              d={`M${ICON_X} ${s.yA} V${endY}`}
              stroke={s.color}
              strokeWidth={10}
              strokeLinecap="round"
            />
            {s.prog > 0.9 && (
              <path
                d={`M${ICON_X - 18} ${s.yB - 22} L${ICON_X} ${s.yB} L${ICON_X + 18} ${s.yB - 22}`}
                stroke={s.color}
                strokeWidth={10}
                strokeLinecap="round"
                strokeLinejoin="round"
                fill="none"
              />
            )}
            {s.prog > 0 && s.prog < 1 && (
              <circle cx={ICON_X} cy={endY} r={16} fill="#fff" />
            )}
          </g>
        );
      })}
    </svg>
  );
};

const Loop: React.FC = () => {
  const frame = useCurrentFrame();
  const t = interpolate(frame, [START + 5 * STEP + 20, START + 6 * STEP + 40], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  const len = 1000;
  const yTop = ROW0 + 2 * PITCH;
  const yBot = ROW0 + 4 * PITCH;
  return (
    <svg width={1080} height={1920} style={{ position: "absolute", inset: 0, opacity: t > 0 ? 1 : 0 }}>
      <path
        d={`M930 ${yBot} H1010 V${yTop} H930`}
        stroke={ORANGE}
        strokeWidth={8}
        strokeDasharray="22 16"
        fill="none"
        strokeLinecap="round"
        pathLength={len}
        style={{ strokeDashoffset: len * (1 - t) }}
      />
      {t > 0.95 && (
        <>
          <path
            d={`M948 ${yTop - 18} L928 ${yTop} L948 ${yTop + 18}`}
            stroke={ORANGE}
            strokeWidth={8}
            strokeLinecap="round"
            strokeLinejoin="round"
            fill="none"
          />
          <text
            transform={`translate(1045 ${(yTop + yBot) / 2}) rotate(90)`}
            textAnchor="middle"
            fontFamily={FONT}
            fontSize={30}
            fill={ORANGE}
            letterSpacing={3}
          >
            REPEATS UNTIL DONE
          </text>
        </>
      )}
    </svg>
  );
};

const Header: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const intro = spring({ frame: frame - 5, fps, config: { damping: 14 } });
  const move = interpolate(frame, [55, 85], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
    easing: Easing.inOut(Easing.cubic),
  });
  const ty = interpolate(move, [0, 1], [560, 0]);
  const sc = interpolate(move, [0, 1], [1.25, 1]);
  return (
    <div
      style={{
        position: "absolute",
        top: 110,
        left: 0,
        right: 0,
        textAlign: "center",
        transform: `translateY(${ty}px) scale(${sc * interpolate(intro, [0, 1], [0.7, 1])})`,
        opacity: Math.min(1, intro * 2),
        transformOrigin: "50% 0",
      }}
    >
      <div style={{ fontFamily: FONT, fontSize: 64, color: "#fff", lineHeight: 1.1 }}>
        WHAT ACTUALLY HAPPENS
        <br />
        WHEN YOU PRESS <span style={{ color: ORANGE }}>ENTER</span>
      </div>
      <div
        style={{
          display: "inline-block",
          marginTop: 24,
          background: ORANGE,
          color: BG,
          fontFamily: FONT,
          fontSize: 54,
          padding: "6px 30px",
          borderRadius: 14,
        }}
      >
        IN CLAUDE CODE
      </div>
    </div>
  );
};

const Outro: React.FC = () => {
  const frame = useCurrentFrame();
  const { fps } = useVideoConfig();
  const s = spring({ frame: frame - (START + 6 * STEP), fps, config: { damping: 12 } });
  if (s <= 0) return null;
  return (
    <div
      style={{
        position: "absolute",
        left: 0,
        right: 0,
        bottom: 60,
        textAlign: "center",
        transform: `scale(${s})`,
        fontFamily: FONT,
        fontSize: 43,
        color: "#fff",
      }}
    >
      PROMPT → MODEL → TOOL → RESULT → ANSWER
    </div>
  );
};

export const Explainer: React.FC = () => {
  const frame = useCurrentFrame();
  const show = interpolate(frame, [75, 90], [0, 1], {
    extrapolateLeft: "clamp",
    extrapolateRight: "clamp",
  });
  return (
    <AbsoluteFill style={{ background: BG }}>
      <Header />
      <div style={{ opacity: show }}>
        <Rail />
        <Loop />
        {STEPS.map((s, i) => (
          <Row key={s.label} step={s} index={i} />
        ))}
        <Outro />
      </div>
    </AbsoluteFill>
  );
};
