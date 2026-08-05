# Perianth Guide
This guide will cover how to use a few features of the tool, as well as likely questions a user may have.

## Getting the tool
Download the release from the release area on the GitHub page. Make sure you get the right one for your device,
Windows or Linux depending on what you're using. You can also build from source yourself, if you like.

Extract the download from zip or archive, and then launch which version of the tool you wish to use (double click!). I'd recommend
using 'perianth-gui' as this is easier to use for beginners, and let's you see things visually.

##  The Tool At A First Glance

The GUI version should open a window with three large panels. You can adjust the width of these as you like.
In the top-right, there is a green button that allows you to switch between Light Mode and Dark Mode. It should save your last
used preference, so use whichever you prefer.

## Finding a file / Browsing the Archive

It is a good idea to make a backup before doing anything, as this tool is still experimental and issues may occur which could lead to corruption. 
Steam allows for making easy backups of your games, so do that first. The tool won't work on the compressed Steam backup itself however.

The left panel of the tool has a green icon, you can use it to browse and find the file path with the game files.
You need to select the folder which has the SDFTOC file in it, it should look something like:

(Path to Your Game)/camel/sdf/pc/data

You can click the folder that says data, and then press 'select'.

If it worked, you should now see at the bottom of the left panel something like '486,543 paths. Type to search.'

To find a file, you can now use the search bar, just type in a name of a prop or character (these often have a prefix like prp_ or chr_ ).
The most useful to you will be files which end in .mmb , and if you click one, the middle panel will display what files are associated with it.

## Viewing Textures for a model
After you've selected a model's .mmb in the browse panel, you can now click 'textures' in the middle panel. This lets you see a 2d representation
of what textures a model is using.

You can scroll down with the scroll bar to look through them, and load more if you reach the bottom of the list.

> NOTE: Many characters and props reference a collection of shared textures, usually simple colours. This will be important should you decide to edit a texture later.

## Exporting a Model as .glb
The game's models use their own file formats for models, important things like the geometry, textures, and other data a model needs
is split up between multiple file types. In addition, these file types can't be understood by the normal tools for viewing assets which people use,
such as Blender. What this tool does is to solve this interoperability issue - and it can convert these file types into a single, readable .glb file.

To do this: 
1. Select an asset's .mmb as described above.
2. Select a folder you wish for the export to be sent to, using the green button at the top of the right panel of the tool.
3. You will usually want to leave the green buttons ticked, so leave them as such for now.
4. Ignore the "Textures from a mod folder" for now.
5. You can select an animation from a drop-down menu.
6. You can select various facial states with their own drop down menu. NOTE: Pressing "Export all" on any of these will automatically export a model with each of the variants
as a .glb file for each, you can view these in something like Blender to see what the numbers correspond to visually.

7. You can select lipsync animation for your model. You can select the language and search by speech ID (which is difficult) or by subtitles used.

You can also write the voice audio as a .wav file to accompany your .glb export. 
*To use this, you will need to have installed 'vgmstream cli' installed and the tool pointing to it.*

It is worth noting that these *Don't correspond to a particular character's voice lines themselves* but rather just what is said. You may end up with your model
using another character's voice entirely. This will likely take some trial and error, and noting down who says a particular speech ID/line.

8. Export and Extract
Extract simply extracts the raw files, and does not convert them, whereas the green *export* *exports what you have selected to a .glb (and optional .wav)*
Press export, and it should both extract to an extracted folder in your destination, and make a .glb in an exports folder in the destination you selected.
Be careful that you don't overwrite your exports with the same name as a previous one, as this may happen automatically!


## Saving a Texture as a .png

1. Select a model .mmb file
2. Press the texture tab
3. Select the texture.
4. Press 'Save as PNG'.
5. Select the save destination and press save.


## Replacing a texture
1. Select a model .mmb file
2. Press the texture tab
3. Select the texture you wish to replace.

IMPORTANT - Many of the game's assets use a shared set of colour textures. Changing the texture in this way, 
and then using something like a mod loader to read it, will override all occurrences of that texture used by assets.
If you want to edit the texture only for a particular asset and use it this way, see the later "Give this model its own copy" steps.

4. Select replace image , and select the image you wish to replace it with.
5. If you wish to simply see the model with an altered texture in .glb format, you can use export GLB without doing anything else special.
6. If you wish to make files for modding with the texture change, you can enter a mod name, author, version, and description - then press write mod.
Note - This should save the new texture as either .png or .dds .
7. If you wish to save as a delta patch instead - press "Save as Patches" . These allow you to make a patch which this tool can read and apply to someone's own version
of the game which they own to recreate the mod you made, and the folder structure, which a mod loader can then read. This is useful for anyone wishing to mod
their own version of a game, and means mods can be made and shared without sharing the game's files themselves. Please see the relevant disclaimer on this
repo however, and any other repo, as you do so at your own risk.

## Replacing a texture for an Asset, and giving the model its own copy.
1. Select a model .mmb file
2. Press the texture tab
3. Select the texture.
4. Select "Give this model its own copy from my image" and select your .png file (A copy may be converted to an uncompressed .dds format afterwards)
5. You can select which particular parts of a model you wish the new texture to apply to. To find out part numbers on a model, simply view an exported .glb version in Blender

Press on the part -> Go to properties editor -> Object Data Properties (the green triangle tab). The name at the very
top should be something like mode3-record-(number).


press on a part, and view its name. Each mesh is named something like Mode2 or Mode3 then -record- then N where N represents the part number you are looking for.
If you wish to apply the new texture to multiple parts, enter each number separated by a comma.
6. You can now either Write Mod or Save as Patches as shown earlier. 
NOTE - This method will should now automatically make a new .editordata file for the mod in the mod folder, or when generated from a patch is made, and allows the new texture to be used.

## Making Patches from a Mod Folder
1. Select Patches near the top right of the tool.
2. Select "Make Patches from a mod folder"
3. Select the folder you wish to make a patch from.
4. Immediately after, select the folder you wish to write the patch too.
5. The patch file will be written automatically.

## Making a Mod from a Patch
To make a mod out of a patch you have:
1. Select Patches near the top right of the tool.
2. Select "Open a folder of patches".
3. Select the folder containing the patch(es).
4. Enter a mod name, author, version, and description to write the manifest.
5. Press "Write Mod" and select the location you wish for the mod to be written into.

From there, follow the latest instructions for the mod loader to be able to load the mod into the game. Please see the relevant disclaimer on this repo and any other
repo however, as you do so at your own risk.

## Loading a .glb export into Blender
1. Open Blender
2. Press File -> Import ->  glTF 2.0 (.glb/.glTF)
3. Select your file and import it.

## General Blender Tips:
These may be outdated, but you can find many useful Blender tutorials online, such as on YouTube, as well as many people online who would be happy to help.

1.To align your view better, press '1' on the numpad. It should make you face the model front on.
2. If your model looks untextured, or like a wireframe of lines, press 'Z' and select Rendered or Material Preview.
3. To remove any annoying lines which may be on or near the model:
  - Press the short downwards v next to 'Overlays' near the top of Blender (it should look like two circles overlapping - one filled in and one not)
  - Untick options like Extras, Relationship Lines, Outline Selected etc. until it looks better for you.
4. If you see weird effects, such as pupils seeming to grow and shrink, Use the Properties Tab -> Render -> Untick 'Temporal Reprojection'
5. If the model looks too dark in Render view, you need to add lighting to your Blender scene.
6. Press 'Spacebar' to play an animation, also you can move the blue icon to move through the frames.
7. To attach a .wav audio clip - Open video sequencer -> Browse scene to be linked -> select the scene 
    -> Now press 'Add' -> Sound -> Select your .wav file -> move it into the channel and correct timing -> click to finish.















