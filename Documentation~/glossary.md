# Glossary

- **Model asset** — The published Unity asset containing one source mesh's vertices, indices, cluster hierarchy and culling metadata.
- **Instance** — A `VirtualMeshInstance` referring to a published model, source renderer, materials and placement transform. Many instances can share one model.
- **Cluster** — A small patch of triangles (also called a meshlet). Indirect draws reference visible clusters.
- **Cluster group** — A collection of related cluster levels with shared LOD selection boundaries.
- **LOD** — Level of detail. The runtime chooses the appropriate cluster level using projected error and related budget and coverage rules.
- **Atlas** — Shared GPU buffers containing geometry and model offsets for admitted models.
- **Residency** — Whether a model's geometry is currently available to draw from GPU buffers. Pending or rejected models can show source color.
- **Hi-Z** — A hierarchy of depth textures used to test whether a cluster is behind already drawn geometry.
- **Bake** — Editor-side conversion from source model to the published cluster assets and prefab.

See [Implementation](implementation.md) for how these parts connect.