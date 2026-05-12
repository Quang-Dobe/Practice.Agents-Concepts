---
name: typescript-frontend-conventions
description: Use this skill whenever writing or reviewing TypeScript code for a frontend learning demo. Defines the coding conventions, type-system rules, design patterns, file layout, and architectural defaults that every TS/React MVP in this repo should follow. Load before the `code-implementer` agent starts a frontend topic, or when reviewing existing frontend code.
---

# TypeScript Frontend Conventions

These conventions apply to every `code/mvp.ts` / `code/mvp.tsx` / `code/*.ts` file generated for a topic in the `frontend/` category.

The goal is **the smallest demo that still feels like real code a senior frontend engineer would write in 2026**. Not a toy. Not a production app. The middle.

## Compiler & runtime

- **TypeScript 5.4+**. Strict mode is mandatory:
  ```json
  // tsconfig.json
  {
    "compilerOptions": {
      "target": "ES2022",
      "module": "ESNext",
      "moduleResolution": "Bundler",
      "strict": true,
      "noUncheckedIndexedAccess": true,
      "exactOptionalPropertyTypes": true,
      "noImplicitOverride": true,
      "skipLibCheck": true,
      "esModuleInterop": true,
      "isolatedModules": true,
      "jsx": "react-jsx"
    }
  }
  ```
- **Runtime**: prefer **`tsx`** (`npx tsx mvp.ts`) for runnable scripts — no build step. For browser demos, use **Vite** with the `react-ts` template if React is in play; otherwise a single HTML file with a `<script type="module" src="mvp.ts">` and Vite.
- **Module system**: ESM only. `import` / `export`. Never `require`.
- **Package manager**: `npm` is the default in `README.md` (lowest friction). `bun` is fine if it makes the demo dramatically simpler.

## Type-system rules

1. **No `any`.** Ever. Use `unknown` when the type is genuinely not known, then narrow.
2. **No non-null assertion (`!`) in demo code.** If the value can be null, the code should show how that's handled.
3. **Prefer `type` aliases over `interface`** for data shapes. Use `interface` only when declaration merging is genuinely needed (rare).
4. **`as const` is your friend** for literal arrays and config objects. Use it whenever you'd otherwise lose narrowness.
5. **Discriminated unions** are the default for state shapes and action types — not enums, not boolean flags. Example:
   ```ts
   type FetchState<T> =
     | { status: "idle" }
     | { status: "loading" }
     | { status: "success"; data: T }
     | { status: "error"; error: Error };
   ```
6. **No `enum`.** Use union of string literals (`type Color = "red" | "green" | "blue"`) or `as const` objects. TypeScript enums emit weird runtime code.
7. **Inference over annotation.** Annotate function parameters and exported function return types. Don't annotate local `const` values that the inferencer already handles.
8. **Use `satisfies`** when you want type-checking without widening — especially for config objects.

## Naming

- Variables, functions, parameters: `camelCase`.
- Types, type aliases, React components: `PascalCase`.
- Constants exported as part of a module's public API: `SCREAMING_SNAKE_CASE`. Local constants inside a function: `camelCase`.
- Boolean variables read like assertions: `isLoading`, `hasNext`, `canEdit`. Not `loading`, `next`, `edit`.
- React event handlers: `handleClick`, `handleSubmit`. Props that accept handlers: `onClick`, `onSubmit`.
- Files: kebab-case for utility/script files (`fetch-user.ts`), PascalCase for files exporting a single component (`UserCard.tsx`).

## Code style

- **2-space indent.** Always.
- **Single quotes** for strings, **backticks** for templates. No double quotes.
- **Trailing commas** in multi-line literals.
- **Arrow functions** for callbacks and component definitions. Named `function` declarations only for hoisting-dependent module-level helpers.
- **Early returns over nested `if`**. Pyramid of doom is forbidden.
- **No default exports** except for top-level page components when the framework requires it (Next.js `page.tsx`). Default exports break refactoring tooling.
- **Imports sorted in this order, separated by a blank line**:
  1. Built-in modules and external packages
  2. Internal absolute imports
  3. Relative imports (`./`, `../`)
  4. Type-only imports (`import type { ... }`)

## React-specific rules (when the topic involves React)

- **Function components only.** No class components.
- **Hooks at the top** of the function body, in this order: state hooks, then effect hooks, then derived/memo hooks, then handlers, then render.
- **Props typed inline or via a named `Props` type** above the component:
  ```tsx
  type ButtonProps = {
    label: string;
    onClick: () => void;
    variant?: "primary" | "ghost";
  };

  export const Button = ({ label, onClick, variant = "primary" }: ButtonProps) => {
    return <button onClick={onClick} className={variant}>{label}</button>;
  };
  ```
- **No `React.FC`.** It adds an implicit `children` and obscures the prop type.
- **Keys must be stable IDs**, not array indices, except for static lists.
- **`useEffect` is a last resort**, not a first reach. Prefer event handlers, derived state, or `useSyncExternalStore` for subscriptions.
- **State colocated with use.** Lift state up only when two siblings need it.

## Design patterns to demonstrate when the topic calls for it

- **Module pattern** — single-purpose file exporting one or two named functions. The default shape of a demo file.
- **Discriminated union state machine** — the default way to model anything with more than two states. See type-system rule 5.
- **Custom hook** — when behavior (subscription, side effect, derived data) needs to be reused across components. Hooks start with `use`.
- **Render prop / children-as-function** — when sharing rendering logic without coupling to a UI shape. Less common than hooks but still right occasionally.
- **Compound components** — for UI primitives with tightly-coupled parts (`Tabs`, `Tabs.List`, `Tabs.Panel`). Use a context internally.
- **Provider pattern** — when state genuinely needs to be available to a subtree. Wrap a context with a typed `useX()` hook that throws if used outside the provider.

## Anti-patterns to avoid in demos

- Importing a UI library (Material UI, Chakra, Ant Design) — never. The demo is about the concept, not the chrome.
- Adding state management (Redux, Zustand, Jotai) when `useState` + `useReducer` would do.
- `useEffect` for data fetching when the topic isn't *about* data fetching — use a top-level `await` in `mvp.ts` or call the function at the bottom of the file.
- Setting up routing for a demo that doesn't need it.
- ESLint / Prettier config files unless the topic is *about* tooling.

## File layout for frontend demos

**Single-file demo** (most topics):
```
code/
├── mvp.ts            # or mvp.tsx if React
├── tsconfig.json
├── package.json
└── README.md
```

**Two-file demo** (browser-rendered):
```
code/
├── index.html        # tiny entry, one <script type="module" src="/mvp.ts">
├── mvp.ts
├── tsconfig.json
├── package.json
└── README.md
```

**React component demo**:
```
code/
├── src/
│   ├── main.tsx      # ReactDOM.createRoot, mounts <App />
│   └── App.tsx       # the actual demo
├── index.html
├── vite.config.ts
├── tsconfig.json
├── package.json
└── README.md
```

Stop here. No `src/components/`, `src/hooks/`, `src/utils/` folders for a demo. Flatten everything until there's a real second use case.

## What a finished frontend MVP should feel like

A reader who knows TypeScript should be able to:
1. Read `mvp.ts` top to bottom in 2 minutes.
2. Run it with `npm install && npx tsx mvp.ts` (or `npm run dev` for browser demos).
3. See the concept actually working.
4. Understand which 3–5 lines are the *load-bearing* ones for the topic, because the comments said so.

If any of those four are not true, the demo needs another pass.
