"""Export selected Enshrouded Blender model(s) as a textured GLB for Bjornhjarta.

Workflow:
1. Import a RenderModel with the separate Enshrouded Blender Tools extension.
2. Select the mesh object(s) that form one building piece.
3. Edit the settings below (especially TYPE_ID and OUTPUT_DIR).
4. Run this script from Blender's Text Editor.

The script exports a copy. It does not edit Enshrouded archives or source models.

Enshrouded RMA images use R=metalness, G=roughness, B=ambient occlusion.
The script can convert detected RMA images into standard glTF ORM channel order:
R=ambient occlusion, G=roughness, B=metalness. This is needed for correct
PBR interpretation by glTF renderers. Detection is name-based by default; use
RMA_IMAGE_OVERRIDES when a material's RMA image has a different name.
"""

from __future__ import annotations

import json
import re
import struct
import tempfile
import uuid
from datetime import datetime, timezone
from pathlib import Path

# -----------------------------------------------------------------------------
# USER SETTINGS
# -----------------------------------------------------------------------------
OUTPUT_DIR = Path.home() / "Documents" / "Bjornhjarta" / "ModelAssets"
ASSET_NAME = "Enshrouded Building Piece"
TYPE_ID = "REPLACE_WITH_EXACT_ENSHROUDED_TYPE_ID"
SOURCE_MODEL_GUID = ""  # Optional: paste the source keen::RenderModel GUID.
NOTES = "Exported from a local Enshrouded Blender Tools import."

# RMA image detection matches image names or file names containing 'rma'.
# Use this dictionary to force a source image for a material if auto-detection
# does not find it. Keys are Blender material names; values are image names.
RMA_IMAGE_OVERRIDES: dict[str, str] = {}
CONVERT_ENSHROUDED_RMA_TO_GLTF_ORM = True
# -----------------------------------------------------------------------------

GLB_MAGIC = b"glTF"
GLB_VERSION = 2
JSON_CHUNK = 0x4E4F534A
BIN_CHUNK = 0x004E4942


def safe_filename(value: str) -> str:
    value = re.sub(r"[^A-Za-z0-9._-]+", "_", value.strip())
    value = value.strip("._")
    return value or "enshrouded_model"


def _read_glb(path: Path) -> tuple[dict, bytes, list[tuple[int, bytes]]]:
    data = path.read_bytes()
    if len(data) < 20:
        raise ValueError("The exported GLB is too small to contain a valid header and JSON chunk.")
    magic, version, total_length = struct.unpack_from("<4sII", data, 0)
    if magic != GLB_MAGIC or version != GLB_VERSION or total_length != len(data):
        raise ValueError("The exported file is not a valid glTF 2.0 GLB container.")

    offset = 12
    document = None
    binary = b""
    extra_chunks: list[tuple[int, bytes]] = []
    while offset + 8 <= len(data):
        chunk_length, chunk_type = struct.unpack_from("<II", data, offset)
        offset += 8
        chunk_end = offset + chunk_length
        if chunk_end > len(data):
            raise ValueError("A GLB chunk extends beyond the container boundary.")
        chunk_data = data[offset:chunk_end]
        if chunk_type == JSON_CHUNK and document is None:
            document = json.loads(chunk_data.decode("utf-8").rstrip(" \t\r\n\0"))
        elif chunk_type == BIN_CHUNK and not binary:
            binary = chunk_data
        else:
            extra_chunks.append((chunk_type, chunk_data))
        offset = chunk_end

    if document is None:
        raise ValueError("The GLB does not contain a JSON chunk.")
    return document, binary, extra_chunks


def _write_glb(path: Path, document: dict, binary: bytes, extra_chunks: list[tuple[int, bytes]]) -> None:
    json_bytes = json.dumps(document, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    json_bytes += b" " * ((-len(json_bytes)) % 4)
    binary = binary + (b"\0" * ((-len(binary)) % 4))

    chunks = [(JSON_CHUNK, json_bytes)]
    if binary:
        chunks.append((BIN_CHUNK, binary))
    chunks.extend(extra_chunks)

    total_length = 12 + sum(8 + len(payload) for _, payload in chunks)
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("wb") as stream:
        stream.write(struct.pack("<4sII", GLB_MAGIC, GLB_VERSION, total_length))
        for chunk_type, payload in chunks:
            stream.write(struct.pack("<II", len(payload), chunk_type))
            stream.write(payload)
    temporary.replace(path)


def patch_glb_with_orm_textures(glb_path: Path, orm_by_material: dict[str, Path]) -> list[str]:
    """Embed ORM PNGs and point glTF material channels to their shared texture.

    The generated glTF ORM convention is R=occlusion, G=roughness, B=metalness.
    Existing GLB chunks and existing material textures are retained; only the
    metallicRoughnessTexture and occlusionTexture references are updated.
    """
    if not orm_by_material:
        return []

    document, binary, extra_chunks = _read_glb(glb_path)
    buffers = document.setdefault("buffers", [])
    if not buffers or not isinstance(buffers[0], dict):
        raise ValueError("The GLB primary buffer entry is missing or invalid.")
    if "uri" in buffers[0]:
        raise ValueError("Cannot safely add embedded ORM maps: GLB primary buffer has an external URI.")

    # GLB's BIN chunk can contain up to three padding bytes beyond byteLength.
    if len(binary) < int(buffers[0].get("byteLength", 0)):
        raise ValueError("The GLB binary chunk is shorter than its declared buffer length.")
    binary_data = bytearray(binary)
    binary_data.extend(b"\0" * ((-len(binary_data)) % 4))

    buffer_views = document.setdefault("bufferViews", [])
    images = document.setdefault("images", [])
    textures = document.setdefault("textures", [])
    materials = document.setdefault("materials", [])
    by_name = {str(material.get("name", "")): material for material in materials if isinstance(material, dict)}

    patched: list[str] = []
    for material_name, png_path in orm_by_material.items():
        material = by_name.get(material_name)
        if material is None:
            print(f"[Bjornhjarta] WARNING: GLB material not found by name; skipped RMA conversion: {material_name}")
            continue
        if not png_path.is_file():
            print(f"[Bjornhjarta] WARNING: Generated ORM image is missing; skipped: {png_path}")
            continue

        png_bytes = png_path.read_bytes()
        if not png_bytes.startswith(b"\x89PNG\r\n\x1a\n"):
            raise ValueError(f"Generated ORM image is not a PNG file: {png_path}")

        byte_offset = len(binary_data)
        buffer_view_index = len(buffer_views)
        buffer_views.append({
            "buffer": 0,
            "byteOffset": byte_offset,
            "byteLength": len(png_bytes),
        })
        binary_data.extend(png_bytes)
        binary_data.extend(b"\0" * ((-len(binary_data)) % 4))

        image_index = len(images)
        images.append({
            "name": f"{material_name}_Bjornhjarta_ORM",
            "bufferView": buffer_view_index,
            "mimeType": "image/png",
        })
        texture_index = len(textures)
        textures.append({
            "name": f"{material_name}_Bjornhjarta_ORM",
            "source": image_index,
        })

        pbr = material.setdefault("pbrMetallicRoughness", {})
        pbr["metallicFactor"] = 1.0
        pbr["roughnessFactor"] = 1.0
        pbr["metallicRoughnessTexture"] = {"index": texture_index}
        material["occlusionTexture"] = {"index": texture_index}
        patched.append(material_name)

    if not patched:
        return []

    # byteLength excludes final BIN chunk padding, as required by glTF 2.0.
    last_view = buffer_views[-1]
    buffers[0]["byteLength"] = int(last_view["byteOffset"]) + int(last_view["byteLength"])
    _write_glb(glb_path, document, bytes(binary_data), extra_chunks)
    return patched


def _find_rma_image(material, bpy):
    override = RMA_IMAGE_OVERRIDES.get(material.name)
    candidates = []
    if material.use_nodes and material.node_tree:
        for node in material.node_tree.nodes:
            if node.type != "TEX_IMAGE" or node.image is None:
                continue
            image = node.image
            identity = f"{image.name} {Path(image.filepath or '').name}".lower()
            candidates.append(image)
            if override and (image.name == override or Path(image.filepath or "").name == override):
                return image
            if not override and "rma" in identity:
                return image
    if override:
        for image in bpy.data.images:
            if image.name == override or Path(image.filepath or "").name == override:
                return image
    return None


def _save_converted_orm_image(source_image, output_path: Path, bpy) -> None:
    color_space = getattr(source_image.colorspace_settings, "name", "")
    if color_space and color_space.lower() not in {"non-color", "raw", "data"}:
        raise ValueError(
            f"RMA image '{source_image.name}' uses color space '{color_space}'. "
            "Set the RMA image to Non-Color in Blender before exporting; data maps must not be treated as sRGB."
        )
    width, height = int(source_image.size[0]), int(source_image.size[1])
    if width <= 0 or height <= 0:
        raise ValueError(f"RMA image has invalid dimensions: {source_image.name}")

    source_pixels = [0.0] * (width * height * 4)
    source_image.pixels.foreach_get(source_pixels)
    orm_pixels = [0.0] * len(source_pixels)
    for index in range(0, len(source_pixels), 4):
        # Enshrouded RMA: R=Metalness, G=Roughness, B=AO.
        # glTF ORM:       R=AO,        G=Roughness, B=Metalness.
        orm_pixels[index] = source_pixels[index + 2]
        orm_pixels[index + 1] = source_pixels[index + 1]
        orm_pixels[index + 2] = source_pixels[index]
        orm_pixels[index + 3] = 1.0

    converted = bpy.data.images.new(
        name=f"{source_image.name}_Bjornhjarta_ORM",
        width=width,
        height=height,
        alpha=True,
        float_buffer=False,
    )
    try:
        try:
            converted.colorspace_settings.name = "Non-Color"
        except Exception:
            pass
        converted.pixels.foreach_set(orm_pixels)
        converted.file_format = "PNG"
        converted.filepath_raw = str(output_path)
        converted.save()
    finally:
        bpy.data.images.remove(converted)


def main() -> None:
    try:
        import bpy
    except ImportError as exc:
        raise RuntimeError("Run this export helper from Blender's Text Editor.") from exc

    selected_meshes = [obj for obj in bpy.context.selected_objects if obj.type == "MESH"]
    if not selected_meshes:
        raise RuntimeError("Select at least one imported Enshrouded mesh object before running this script.")
    if not TYPE_ID or TYPE_ID.startswith("REPLACE_"):
        raise RuntimeError("Set TYPE_ID to the exact Enshrouded Type ID before exporting.")
    if not hasattr(bpy.ops.export_scene, "gltf"):
        raise RuntimeError("Blender's glTF 2.0 exporter is unavailable. Enable the glTF 2.0 exporter and retry.")

    output_dir = Path(OUTPUT_DIR).expanduser().resolve()
    output_dir.mkdir(parents=True, exist_ok=True)
    file_stem = safe_filename(ASSET_NAME)
    model_path = output_dir / f"{file_stem}.glb"
    manifest_path = output_dir / f"{file_stem}.bjornhjarta.asset.json"
    if model_path.exists() or manifest_path.exists():
        suffix = uuid.uuid4().hex[:8]
        model_path = output_dir / f"{file_stem}_{suffix}.glb"
        manifest_path = output_dir / f"{file_stem}_{suffix}.bjornhjarta.asset.json"

    bpy.ops.export_scene.gltf(
        filepath=str(model_path),
        export_format="GLB",
        use_selection=True,
    )
    if not model_path.is_file() or model_path.stat().st_size < 20:
        raise RuntimeError("Blender did not produce a valid GLB file. Check the export log.")

    converted_materials: list[str] = []
    conversion_complete = False
    source_packing = "Unknown or not identified"
    target_packing = "Standard glTF ORM: R=occlusion, G=roughness, B=metalness"
    if CONVERT_ENSHROUDED_RMA_TO_GLTF_ORM:
        material_to_image = {}
        for obj in selected_meshes:
            for material in obj.data.materials:
                if material is None or material.name in material_to_image:
                    continue
                rma_image = _find_rma_image(material, bpy)
                if rma_image is not None:
                    material_to_image[material.name] = rma_image

        if material_to_image:
            try:
                with tempfile.TemporaryDirectory(prefix="bjornhjarta_orm_") as temp_dir:
                    orm_by_material = {}
                    for material_name, source_image in material_to_image.items():
                        orm_path = Path(temp_dir) / f"{safe_filename(material_name)}_ORM.png"
                        _save_converted_orm_image(source_image, orm_path, bpy)
                        orm_by_material[material_name] = orm_path
                    converted_materials = patch_glb_with_orm_textures(model_path, orm_by_material)
                conversion_complete = len(converted_materials) == len(material_to_image) and bool(material_to_image)
                source_packing = "Enshrouded RMA: R=metalness, G=roughness, B=ambient occlusion"
                if conversion_complete:
                    print("[Bjornhjarta] Converted RMA → glTF ORM for: " + ", ".join(converted_materials))
                else:
                    missing = sorted(set(material_to_image) - set(converted_materials))
                    source_packing += "; partial or skipped material conversion: " + ", ".join(missing)
                    target_packing = "Partially converted or not converted - manual material review required"
            except Exception as exc:
                source_packing = f"Enshrouded RMA detected; conversion failed: {exc}"
                target_packing = "Not converted - manual material review required"
                converted_materials = []
                conversion_complete = False
                print("[Bjornhjarta] WARNING: RMA conversion was not completed: " + str(exc))
        else:
            print("[Bjornhjarta] WARNING: No RMA image found by name. Exported materials were left unchanged.")
            print("[Bjornhjarta] Set RMA_IMAGE_OVERRIDES to map Blender material names to their RMA image names.")

    manifest = {
        "schemaVersion": 1,
        "assetId": uuid.uuid4().hex,
        "displayName": ASSET_NAME,
        "typeId": TYPE_ID,
        "sourceModelGuid": SOURCE_MODEL_GUID or None,
        "modelFile": model_path.name,
        "source": "Enshrouded Blender Tools / Blender glTF 2.0 exporter",
        "createdUtc": datetime.now(timezone.utc).isoformat(),
        "notes": NOTES,
        "sourceTexturePacking": source_packing,
        "gltfTexturePacking": target_packing,
        "texturePackingConverted": conversion_complete,
    }
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    print("\n[Bjornhjarta] Model asset export completed")
    print(f"GLB:      {model_path}")
    print(f"Manifest: {manifest_path}")
    print(f"Meshes:   {len(selected_meshes)} selected")
    print(f"RMA materials converted: {len(converted_materials)}")
    print("Next: import the GLB in Blueprint Forge > Model Asset Library.")


if __name__ == "__main__":
    main()
