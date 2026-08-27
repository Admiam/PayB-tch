/**
 * The debt-flow drawing, ported from `DebtFlowCanvas.swift`.
 *
 * Participants sit on a ring; each debt is a dashed pink arrow curving
 * outward from debtor to creditor, with dashes marching along it.
 *
 * The geometry is reproduced exactly, but the canvas is responsive rather than
 * a fixed 320px square: on iOS the labels overlap once a group has six or more
 * people, and a phone browser can be much narrower than 320px. The viewBox
 * keeps the maths identical while letting the box scale.
 */

import { useMemo } from "react";
import { Avatar } from "./Avatar";
import type { DebtEdge } from "@/domain/balances";
import type { Member } from "@/domain/types";

/** The coordinate space the geometry is expressed in. */
const SIZE = 320;
const CENTER = SIZE / 2;
const RADIUS = SIZE * 0.36;

interface Point {
  x: number;
  y: number;
}

/** Pulls `target` back toward `origin` by `distance` px. */
function shorten(origin: Point, target: Point, distance: number): Point {
  const vx = target.x - origin.x;
  const vy = target.y - origin.y;
  const length = Math.max(Math.hypot(vx, vy), 0.001);
  return {
    x: target.x - (vx / length) * distance,
    y: target.y - (vy / length) * distance,
  };
}

export function DebtFlowCanvas({
  edges,
  members,
  currentUserId,
}: {
  edges: DebtEdge[];
  members: Member[];
  currentUserId?: string | null;
}) {
  const { positions, participantIds, arrows } = useMemo(() => {
    // Insertion order across the edge array — debtor first, then creditor.
    const ids: string[] = [];
    for (const edge of edges) {
      if (!ids.includes(edge.from)) ids.push(edge.from);
      if (!ids.includes(edge.to)) ids.push(edge.to);
    }

    const pos = new Map<string, Point>();
    ids.forEach((id, index) => {
      // Index 0 sits at 12 o'clock; y grows downward, so index walks clockwise.
      const angle = -Math.PI / 2 + (index / ids.length) * Math.PI * 2;
      pos.set(id, {
        x: CENTER + Math.cos(angle) * RADIUS,
        y: CENTER + Math.sin(angle) * RADIUS,
      });
    });

    const maxAmount = Math.max(...edges.map((e) => e.amount), 1);

    const drawn = edges.flatMap((edge) => {
      const a = pos.get(edge.from);
      const b = pos.get(edge.to);
      if (!a || !b) return [];

      const mid = { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 };
      // Push the control point radially outward so parallel debts don't
      // overlap. With exactly two participants this lands on the centre and
      // the curve degenerates to a straight line, same as on iOS.
      const control = {
        x: mid.x + (mid.x - CENTER) * 0.35,
        y: mid.y + (mid.y - CENTER) * 0.35,
      };

      const end = shorten(control, b, 30);
      const start = shorten(control, a, 28);

      // Arrowhead: a triangle on the tangent at the curve's end.
      const dx = end.x - control.x;
      const dy = end.y - control.y;
      const length = Math.max(Math.hypot(dx, dy), 0.001);
      const ux = dx / length;
      const uy = dy / length;
      const head = 7;
      const spread = head * 0.6;

      const points = [
        `${end.x},${end.y}`,
        `${end.x - ux * head - uy * spread},${end.y - uy * head + ux * spread}`,
        `${end.x - ux * head + uy * spread},${end.y - uy * head - ux * spread}`,
      ].join(" ");

      return [
        {
          id: edge.id,
          d: `M ${start.x} ${start.y} Q ${control.x} ${control.y} ${end.x} ${end.y}`,
          width: 2 + (edge.amount / maxAmount) * 4,
          points,
        },
      ];
    });

    return { positions: pos, participantIds: ids, arrows: drawn };
  }, [edges]);

  if (participantIds.length === 0) return null;

  return (
    <div className="pb-flow">
      <svg
        className="pb-flow__svg"
        viewBox={`0 0 ${SIZE} ${SIZE}`}
        role="img"
        aria-label={`Debt flow between ${participantIds.length} people`}
      >
        {arrows.map((arrow) => (
          <g key={arrow.id}>
            <path
              d={arrow.d}
              fill="none"
              stroke="var(--pb-pink)"
              strokeWidth={arrow.width}
              strokeLinecap="round"
              strokeLinejoin="round"
              strokeDasharray="6 6"
              className="pb-flow__path"
            />
            <polygon points={arrow.points} fill="var(--pb-pink)" />
          </g>
        ))}
      </svg>

      {participantIds.map((id) => {
        const point = positions.get(id);
        const member = members.find((m) => m.id === id);
        // The arrow still draws for a member whose record is gone; only the
        // label is skipped, matching the phone app.
        if (!point || !member) return null;

        return (
          <div
            key={id}
            className="pb-flow__node"
            style={{
              left: `${(point.x / SIZE) * 100}%`,
              top: `${((point.y + 4) / SIZE) * 100}%`,
            }}
          >
            <Avatar member={member} size={48} isMe={id === currentUserId} />
            <span className="pb-flow__name">
              {id === currentUserId ? "Me" : member.name}
            </span>
          </div>
        );
      })}
    </div>
  );
}
