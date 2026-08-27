/**
 * SF Symbol → web icon bridge.
 *
 * The stored data uses SF Symbol names (that is what the phone app writes into
 * `iconSymbol`), so records stay portable between the two apps. This module is
 * the only place that knows how those names become pictures.
 *
 * Every Lucide name below was checked against the installed package rather than
 * guessed. Two SF symbols have no Lucide equivalent at all and are drawn here
 * as inline SVG in Lucide's style so they don't silently fall back to a wrong
 * animal.
 */

import {
  ArrowDown,
  ArrowRight,
  ArrowUp,
  Balloon,
  Beer,
  BedDouble,
  Bike,
  Bird,
  Briefcase,
  BriefcaseMedical,
  Bug,
  Bus,
  Cake,
  Calendar,
  Camera,
  Car,
  Carrot,
  Cat,
  ChevronRight,
  ChevronsUpDown,
  Circle,
  CircleCheckBig,
  CircleDollarSign,
  CircleQuestionMark,
  Clock,
  Coffee,
  Contrast,
  CreditCard,
  Crown,
  CupSoda,
  Dog,
  Dumbbell,
  FaceSlightlySmiling,
  FileText,
  Film,
  Fish,
  Flame,
  Footprints,
  Fuel,
  Gamepad2,
  Gift,
  GraduationCap,
  Guitar,
  Heart,
  House,
  Key,
  Leaf,
  Lightbulb,
  Menu,
  Moon,
  Music,
  Package,
  PartyPopper,
  PawPrint,
  Pencil,
  Pill,
  Plane,
  Plus,
  Popcorn,
  Rabbit,
  Sailboat,
  Scooter,
  Search,
  Settings,
  Shirt,
  ShoppingBag,
  ShoppingCart,
  Sparkles,
  SquarePen,
  Star,
  Sun,
  Ticket,
  TramFront,
  Trash,
  Turtle,
  Users,
  UsersRound,
  Utensils,
  Volleyball,
  Wallet,
  WashingMachine,
  Wine,
  Wrench,
  X,
  Zap,
  type LucideProps,
} from "lucide-react";
import type { ComponentType } from "react";

type IconComponent = ComponentType<LucideProps>;

/* ── The two SF symbols Lucide simply does not have ──────────────────────
   Drawn to Lucide's conventions (24×24 viewBox, currentColor stroke, width 2,
   round caps and joins) so they sit correctly next to the real ones. */

const Teddybear: IconComponent = (props) => (
  <svg
    xmlns="http://www.w3.org/2000/svg"
    width={props.size ?? 24}
    height={props.size ?? 24}
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth={props.strokeWidth ?? 2}
    strokeLinecap="round"
    strokeLinejoin="round"
    className={props.className}
    aria-hidden="true"
  >
    <circle cx="6.5" cy="5.5" r="2.5" />
    <circle cx="17.5" cy="5.5" r="2.5" />
    <path d="M12 21a7 7 0 0 0 7-7 7 7 0 0 0-14 0 7 7 0 0 0 7 7Z" />
    <path d="M10 13h.01M14 13h.01" />
    <path d="M12 16a1.5 1.5 0 0 1-1.5-1.5h3A1.5 1.5 0 0 1 12 16Z" />
  </svg>
);

const Lizard: IconComponent = (props) => (
  <svg
    xmlns="http://www.w3.org/2000/svg"
    width={props.size ?? 24}
    height={props.size ?? 24}
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth={props.strokeWidth ?? 2}
    strokeLinecap="round"
    strokeLinejoin="round"
    className={props.className}
    aria-hidden="true"
  >
    <path d="M4 18c3 0 4-2 6-2s3 2 6 2 4-2 4-4-2-4-5-4c-2 0-3 1-5 1s-3-3-6-3" />
    <path d="M19 10c1.5 0 2.5-1 2.5-2" />
    <path d="M9 20c0 1 1 1.5 2 1.5" />
    <path d="M15 20c0 1 1 1.5 2 1.5" />
    <circle cx="17.5" cy="9" r=".5" fill="currentColor" />
  </svg>
);

/**
 * The mapping. Keys are SF Symbol names exactly as the phone app stores them.
 *
 * Known imperfect matches, kept deliberately rather than hidden:
 *  - `figure.run` → Footprints (Lucide has no running figure at all)
 *  - `ladybug.fill` and `ant.fill` both → Bug
 *  - `basketball.fill` and `soccerball` both → Volleyball (the only ball icon)
 */
const SYMBOLS: Record<string, IconComponent> = {
  // Food & drink
  "fork.knife": Utensils,
  "cup.and.saucer.fill": Coffee,
  "mug.fill": Beer,
  "wineglass.fill": Wine,
  "takeoutbag.and.cup.and.straw.fill": CupSoda,
  "carrot.fill": Carrot,
  "birthday.cake.fill": Cake,
  "popcorn.fill": Popcorn,

  // Transport
  "car.fill": Car,
  "fuelpump.fill": Fuel,
  "tram.fill": TramFront,
  "bus.fill": Bus,
  airplane: Plane,
  bicycle: Bike,
  "ferry.fill": Sailboat,
  scooter: Scooter,

  // Shopping
  "cart.fill": ShoppingCart,
  "bag.fill": ShoppingBag,
  "gift.fill": Gift,
  "tshirt.fill": Shirt,
  "shippingbox.fill": Package,
  "creditcard.fill": CreditCard,

  // Home
  "house.fill": House,
  "bed.double.fill": BedDouble,
  "lightbulb.fill": Lightbulb,
  "washer.fill": WashingMachine,
  "wrench.and.screwdriver.fill": Wrench,
  "key.fill": Key,

  // Fun
  "ticket.fill": Ticket,
  "gamecontroller.fill": Gamepad2,
  "music.note": Music,
  "film.fill": Film,
  sparkles: Sparkles,
  "party.popper.fill": PartyPopper,

  // Health & pets
  "cross.case.fill": BriefcaseMedical,
  "pills.fill": Pill,
  "heart.fill": Heart,
  "figure.run": Footprints,
  "pawprint.fill": PawPrint,
  "dumbbell.fill": Dumbbell,

  // Misc
  "wallet.bifold.fill": Wallet,
  "dollarsign.circle.fill": CircleDollarSign,
  "doc.text.fill": FileText,
  "graduationcap.fill": GraduationCap,
  "briefcase.fill": Briefcase,
  "questionmark.circle.fill": CircleQuestionMark,

  // Avatar-only symbols
  "cat.fill": Cat,
  "dog.fill": Dog,
  "teddybear.fill": Teddybear,
  "hare.fill": Rabbit,
  "tortoise.fill": Turtle,
  "bird.fill": Bird,
  "fish.fill": Fish,
  "lizard.fill": Lizard,
  "ladybug.fill": Bug,
  "ant.fill": Bug,
  "leaf.fill": Leaf,
  "face.smiling": FaceSlightlySmiling,
  "star.fill": Star,
  "crown.fill": Crown,
  "bolt.fill": Zap,
  "flame.fill": Flame,
  "moon.fill": Moon,
  "sun.max.fill": Sun,
  "balloon.fill": Balloon,
  "guitars.fill": Guitar,
  "sailboat.fill": Sailboat,
  "basketball.fill": Volleyball,
  soccerball: Volleyball,
  "camera.fill": Camera,

  // UI chrome
  plus: Plus,
  xmark: X,
  "chevron.right": ChevronRight,
  "arrow.right": ArrowRight,
  "arrow.down": ArrowDown,
  "arrow.up": ArrowUp,
  "checkmark.circle.fill": CircleCheckBig,
  circle: Circle,
  "line.3.horizontal": Menu,
  calendar: Calendar,
  "pencil.circle.fill": SquarePen,
  "chevron.up.chevron.down": ChevronsUpDown,
  trash: Trash,
  "person.3": Users,
  "person.3.sequence": UsersRound,
  clock: Clock,
  "questionmark.circle": CircleQuestionMark,
  pencil: Pencil,
  magnifyingglass: Search,
  gearshape: Settings,
  "circle.lefthalf.filled": Contrast,
  "sun.max": Sun,
};

export interface IconProps {
  /** SF Symbol name, as stored by the phone app. */
  name: string;
  size?: number;
  strokeWidth?: number;
  className?: string;
}

/** Renders an SF Symbol name. Unknown names fall back to the wallet glyph. */
export function Icon({ name, size = 20, strokeWidth = 2, className }: IconProps) {
  const Component = SYMBOLS[name] ?? Wallet;
  return (
    <Component size={size} strokeWidth={strokeWidth} className={className} />
  );
}

export function hasSymbol(name: string): boolean {
  return name in SYMBOLS;
}

/* ── Catalogues, in the same order the phone app presents them ───────── */

export interface IconCategory {
  label: string;
  symbols: string[];
}

export const ICON_CATEGORIES: IconCategory[] = [
  {
    label: "Food & drink",
    symbols: [
      "fork.knife",
      "cup.and.saucer.fill",
      "mug.fill",
      "wineglass.fill",
      "takeoutbag.and.cup.and.straw.fill",
      "carrot.fill",
      "birthday.cake.fill",
      "popcorn.fill",
    ],
  },
  {
    label: "Transport",
    symbols: [
      "car.fill",
      "fuelpump.fill",
      "tram.fill",
      "bus.fill",
      "airplane",
      "bicycle",
      "ferry.fill",
      "scooter",
    ],
  },
  {
    label: "Shopping",
    symbols: [
      "cart.fill",
      "bag.fill",
      "gift.fill",
      "tshirt.fill",
      "shippingbox.fill",
      "creditcard.fill",
    ],
  },
  {
    label: "Home",
    symbols: [
      "house.fill",
      "bed.double.fill",
      "lightbulb.fill",
      "washer.fill",
      "wrench.and.screwdriver.fill",
      "key.fill",
    ],
  },
  {
    label: "Fun",
    symbols: [
      "ticket.fill",
      "gamecontroller.fill",
      "music.note",
      "film.fill",
      "sparkles",
      "party.popper.fill",
    ],
  },
  {
    label: "Health & pets",
    symbols: [
      "cross.case.fill",
      "pills.fill",
      "heart.fill",
      "figure.run",
      "pawprint.fill",
      "dumbbell.fill",
    ],
  },
  {
    label: "Misc",
    symbols: [
      "wallet.bifold.fill",
      "dollarsign.circle.fill",
      "doc.text.fill",
      "graduationcap.fill",
      "briefcase.fill",
      "questionmark.circle.fill",
    ],
  },
];

/** Avatar glyph choices, animals first — same order as the phone app. */
export const AVATAR_SYMBOLS: string[] = [
  "cat.fill",
  "dog.fill",
  "teddybear.fill",
  "pawprint.fill",
  "hare.fill",
  "tortoise.fill",
  "bird.fill",
  "fish.fill",
  "lizard.fill",
  "ladybug.fill",
  "ant.fill",
  "leaf.fill",
  "face.smiling",
  "star.fill",
  "heart.fill",
  "crown.fill",
  "bolt.fill",
  "flame.fill",
  "moon.fill",
  "sun.max.fill",
  "gift.fill",
  "balloon.fill",
  "party.popper.fill",
  "gamecontroller.fill",
  "guitars.fill",
  "bicycle",
  "car.fill",
  "airplane",
  "sailboat.fill",
  "basketball.fill",
  "soccerball",
  "camera.fill",
];

/**
 * Guesses a category icon from the expense title, mirroring
 * `PaybitchIcon.symbol(forTitle:)`. Keyword order matters — the first match
 * wins, so "wine bar" resolves to the drinks glyph, not the food one.
 */
export function symbolForTitle(title: string): string {
  const s = title.toLowerCase();
  const has = (...words: string[]) => words.some((w) => s.includes(w));

  if (has("pizza", "burger", "sushi", "lunch", "dinner", "food", "restaurant", "breakfast"))
    return "fork.knife";
  if (has("coffee", "café", "cafe", "espresso")) return "cup.and.saucer.fill";
  if (has("beer", "bar", "drinks", "pub", "wine", "cocktail")) return "mug.fill";
  if (has("gas", "fuel", "petrol", "benzín")) return "fuelpump.fill";
  if (has("grocer", "market", "shop", "store")) return "cart.fill";
  if (has("hotel", "airbnb", "stay", "rent")) return "bed.double.fill";
  if (has("snack", "popcorn", "movie", "cinema", "film")) return "popcorn.fill";
  if (has("uber", "taxi", "ride", "train", "bus", "flight", "travel"))
    return "tram.fill";
  if (has("ticket", "event", "concert")) return "ticket.fill";
  return "wallet.bifold.fill";
}

/** The icon an expense should show: explicit choice wins, else the guess. */
export function symbolForExpense(expense: {
  iconSymbol?: string | null;
  title: string;
}): string {
  if (expense.iconSymbol) return expense.iconSymbol;
  return symbolForTitle(expense.title);
}
