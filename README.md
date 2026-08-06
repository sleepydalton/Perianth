# Perianth

A **unofficial** asset tool for *South Park: The Fractured But Whole*. It allows for interoperability with the game's file formats for assets by allowing them to be converted into .glb files.

Perianth reads the game's model, material, animation, lip-sync, texture and
archive formats. It can turn a character into a Blender-compatible GLB, edit a
texture and give you back a working mod, and share that mod as a patch so
nobody has to pass around the game's own files.

It refuses inputs it cannot prove it understands rather than guessing at them.

**Status.** Export, extraction, texture and material editing, patches and the
window all work. Writing the *model* format back — turning a GLB into a `.mmb`
— is the eventual goal and is not built.

**The tool is experimental and in early development. Users are advised to make
back-ups before usage.**

> **This tool is not an official tool.** It is not associated with Ubisoft, Massive Entertainment
> or South Park Studios, or anyone else who has developed *South Park: The
> Fractured But Whole*. All relevant copyright rights belong to their
> respective owners, not to me. The tool was primarily made for research and
> interoperability between file formats, and as a fun non-commercial project
> for myself to attempt.
> Extracting, Editing, or Modding the game or its assets or files may violate
> its EULA - use at your own risk.

## How To Get The Tool

Download the build for your platform and run it. Nothing to install: each
download is a single file with no runtime or prerequisites.

- `perianth-gui` — the window. Start here.
- `perianth` — the same capabilities from a command line.

To build it yourself instead, you need the **.NET 10 SDK**:

```console
dotnet build
dotnet publish src/Perianth.Gui -c Release -r win-x64 --self-contained -o out
```

Use `linux-x64` for Linux. One optional extra: **`vgmstream-cli`**, only for
decoding voice audio. Everything else works without it.

The tests need nothing but the SDK:

```console
dotnet test
```

They are deliberately **asset-free**, so they run on a machine that has never
seen the game. Four suites check the readers against the game's real files
instead — DDS, PNG, SDF archives and editordata — and those skip unless you
point them at your own copy, so a normal run reports a dozen or so skipped and
that is correct rather than broken. Each one names the environment variable it
wants in its own source.

NOTE: Although not a requirement, it is very useful for you to have something like
Blender so you can view the .glb files , as no 3D preview is available in the tool currently.

## The window

Open it, point it at your game's `camel/sdf/pc/data` folder, and it reads the
archives. From there:

- **Browse** for a character by name, and see what it resolves to — companions,
  the setup animation that poses it, facial atlases, animation clips.
- **Textures** shows what a model is actually painted with, and offers three
  different things. *Replace from PNG* changes a texture for **every** model
  that uses it — the shipped art is a shared library, so that is usually more
  than one. *Use a new image here* adds your image under a path of its own and
  points **only this model** at it. *Repoint* and *Recolour* change what this
  model uses without touching an image at all.
- **Export** to GLB, with as much or as little as you want: posed or not,
  materials, a specific animation, a facial expression, lip-sync to a voice
  line you can search for by what it says. It can export with **your own
  textures** applied — unsaved edits from the Textures tab, or a mod folder you
  wrote earlier — so you can look at a change in Blender before ever loading it
  in the game.
- **Patches** makes a shareable patch out of a mod folder, or applies patches
  somebody sent you.


### Posing, and why an export can look broken

A model's parts are stored as a flat pile — every alternate state at once, and
no hierarchy placing them. An animation is what selects between them and puts
them where they belong. So **an unposed export looks wrong, but really is not**: pieces
missing, pieces doubled over each other, art that reads mirrored. If you see
that, you may have exported without a pose.

**A character** has a setup animation, found for you, and it is used by default.
There may be a few characters, such as some animals, which lack this and may still
export incorrectly.

**A prop has no setup animation** — none in the game does; that convention is a
character one. Instead it has its **own animations**, usually an **intact idle** and
one or more damaged states. Pick one from the *Animation* box, and an idle is
usually the resting state you want. Which of them is "the" resting pose is not
recorded anywhere, so the tool offers them rather than choosing.

## The command line (an alternative way of using it)

Five verbs. `perianth` with no arguments lists every option for each.

**Find something**, when you do not know where it lives:

```console
perianth extract --sdf-root <game>/camel/sdf/pc/data --find cartman
```

**Take a model and everything it needs** — companions, setup, facial atlases,
clips, the shared lip-sync database — in one step:

```console
perianth extract --sdf-root <game>/camel/sdf/pc/data \
  --character camel/baked/assets/characters/npc/cartman/chr_cartman.mmb \
  --out ./kit
```

It says what it matched and why before writing anything, and records where
every file came from. The tree it writes mirrors the archive's own paths, so it
is both a valid `--content-root` and the layout a loose-file mod loader reads.

**Export**, naming the files explicitly:

```console
perianth export --mmb ./kit/.../chr_cartman.mmb \
  --cameldata ./kit/.../chr_cartman.cameldata \
  --editordata ./kit/.../chr_cartman.editordata --content-root ./kit \
  --setup-anim ./kit/.../anm_cartman_setup.anim \
  --out cartman.glb
```

**Turn an edited image into a mod**:

```console
perianth texture --from repainted.png --original ./kit/.../tex_thing.dds \
  --out ./mods --name "My mod" --author me
```

**Change what a part is painted with**, without editing an image:

```console
perianth material --editordata ./kit/.../chr_cartman.editordata \
  --repoint 'camel/.../tex_ashgray_d.dds=camel/mods/tex_myblue_d.dds' --dry-run
```

One texture is usually bound by dozens or hundreds of parts, so `--dry-run`
first — it says how many it would change and writes nothing. Drop it, add
`--out` and `--name`, and you get a mod.

The other half, for parts drawn on a blank sheet and coloured by a tint:

```console
perianth material --editordata ./kit/.../chr_cartman.editordata \
  --retint 'camel/.../tex_white16_d.dds=0.1,0.2,0.8' --only-tint 0,0,0 --dry-run
```

`--only-tint` narrows it to the parts already that colour. Without it every
part sharing that texture becomes one colour, which on a character flattens the
line work into a silhouette.

**Put a texture on one part only.** Export the model to GLB, open it in
Blender, and click the part — its mesh is named `mode3-record-47`. That number
is what to name:

```console
perianth material --editordata … --assign camel/.../tex_mine_d.dds --section 47
```

`--assign` paints the named parts with that texture, whatever they were carrying
before — you do not have to know what that was. In the window it is the same:
type `47` in the *Parts* box, and the button becomes "Paint part 47 with my
image…".

**See a change before the game does.** The window's Export tab has *Include my
texture changes*; on the command line, point `--content-root` at your mod
folder and `--editordata` at the copy inside it:

```console
perianth export --mmb <model>.mmb --cameldata <model>.cameldata \
  --editordata ./mods/"My mod"/camel/.../<model>.editordata \
  --content-root ./mods/"My mod" --sdf-root <game>/camel/sdf/pc/data \
  --setup-anim <the animation that poses it> --out preview.glb
```

**Check the finished mod before you install it:**

```console
perianth material --verify ./mods/"My mod" --sdf-root <game>/camel/sdf/pc/data
```

If you pointed a part at a texture and then misspelled the path when adding
that texture, the mod still installs and still loads — it just draws the wrong
thing, and says nothing. This is what catches that.

**Make a patch to share it**, and apply one you were given:

```console
perianth patch --make --edited ./mods/.../tex_thing.dds \
  --original ./kit/.../tex_thing.dds --out ./patches

perianth patch --apply --patch ./patches/tex_thing.perianthpatch \
  --original ./kit/.../tex_thing.dds --out ./mods --name "Their mod"
```

A texture of your own that the game never had has no original to differ from,
so `--new` carries it whole:

```console
perianth patch --make --new --edited my_new_texture.dds \
  --replaces camel/.../tex_mine_d.dds --out ./patches
```

That is your file, so shipping all of it is fine — only **the game's own bytes
must stay with whoever owns the game**. Applying it needs no original, and a set
mixing both kinds works: give one `--original` per patch that needs one.

Nothing is resolved behind your back: `export` only ever reads the files you
name, and where a naming convention runs out the tool says so rather than
guessing.

## Two things worth knowing

**Textures are written uncompressed.** The game accepts them, which means you
need no block-compression plugin for your image editor — edit a PNG and this
converts it. The file is larger than the game's own; nothing else should (hopefully) differ.
If your editor does read DDS, you can hand an edited `.dds` straight back
instead, and it is used exactly as you saved it.

**A patch carries only what changed.** Applying one needs the original from
your own copy of the game, which is the point.

**A model is painted two ways, and the tool says which.** Roughly half a
character's parts are scanned sheets of coloured paper, where the colour is in
the image — repointing changes those. The other half are a blank white sheet
coloured by a tint, where repointing would swap one blank sheet for another —
recolouring changes those. The Textures tab tells you which one you have
selected, because picking the wrong edit is not an error; it silently does
nothing.

## Licence

Perianth is MIT licensed — see [LICENSE](LICENSE). That covers this tool's own
source only, and grants no rights to the game or its assets.

A published build bundles Avalonia, SkiaSharp, HarfBuzzSharp and the Inter
typeface; their licences are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

This is a tool for working with assets you already own. It ships no game
content, and it never will. Please do not ask me for game content — I cannot
share it.

**Legal disclaimer:**

1. **Do not upload, re-host, share, or distribute original game assets or
   copyrighted material.**

2. **This tool does not include or ship with any copyrighted content from
   South Park: The Fractured But Whole.**

3. **Please respect the copyright and relevant laws around all game assets,
   models, audio and other materials.**
