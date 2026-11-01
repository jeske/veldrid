Documentation Archive

![Image 1](https://developer.apple.com/library/archive/Resources/1282/Images/apple2.png)Developer

Metal Best Practices Guide

![Image 2](https://developer.apple.com/library/archive/Resources/1282/Images/shortstack_2x.png)

*   Metal Best Practices
    *   Fundamental Concepts

*   Resource Management
    *   Persistent Objects
    *   Resource Options
    *   Triple Buffering
    *   Buffer Bindings

*   Display Management
    *   Drawables
    *   Native Screen Scale (iOS and tvOS)
    *   Frame Rate (iOS and tvOS)

*   Command Generation
    *   Load and Store Actions
    *   Render Command Encoders (iOS and tvOS)
    *   Command Buffers
    *   Indirect Buffers

*   Compilation
    *   Functions and Libraries
    *   Pipelines

*   Revision History
    *   Document Revision History

On This Page
*   Familiarize Yourself with Device Memory Models
*   Choose an Appropriate Resource Storage Mode (iOS and tvOS)
*   Choose an Appropriate Resource Storage Mode (macOS)
*   Set Appropriate Texture Usage Flags

## Resource Options

**Best Practice:** Set appropriate resource storage modes and texture usage options.

Your Metal resources must be configured appropriately to take advantage of fast memory access and driver performance optimizations. Resource storage modes allow you to define the storage location and access permissions for your `MTLBuffer` and `MTLTexture` objects. Texture usage options allow you to explicitly declare how you intend to use your `MTLTexture` objects.

### Familiarize Yourself with Device Memory Models

Device memory models vary by operating system. iOS and tvOS devices support a _unified_ memory model in which the CPU and the GPU share system memory. macOS devices support a _discrete_ memory model with CPU-accessible system memory and GPU-accessible video memory.

Important

Some macOS devices feature integrated GPUs. In these devices, the driver optimizes the underlying architecture to support a discrete memory model. macOS Metal apps should always target a discrete memory model.

All iOS and tvOS devices feature integrated GPUs.

### Choose an Appropriate Resource Storage Mode (iOS and tvOS)

In iOS and tvOS, the `Shared` mode defines system memory accessible to both the CPU and the GPU, whereas the `Private` mode defines system memory accessible only to the GPU.

The `Shared` mode is usually the correct choice for iOS and tvOS resources. Choose the `Private` mode only if the CPU never accesses your resource.

Note

In iOS and tvOS, the `memoryless` storage mode is provided for memoryless textures. This storage mode can only be used for temporary render targets stored in on-chip tile memory. For further information, see `Memoryless Textures` in the _Metal Programming Guide_.

**Figure 3-1**Resource storage modes in iOS and tvOS![Image 3: image: ../Art/ResourceManagement_iOStvOSMemory.pdf](https://developer.apple.com/library/archive/documentation/3DDrawing/Conceptual/MTLBestPracticesGuide/Art/ResourceManagement_iOStvOSMemory_2x.png)
### Choose an Appropriate Resource Storage Mode (macOS)

In macOS, the `Shared` mode defines system memory accessible to both the CPU and the GPU, whereas the `Private` mode defines video memory accessible only to the GPU.

Additionally, macOS implements the `Managed` mode that defines a synchronized memory pair for a resource, with one copy in system memory and another in video memory. Managed resources benefit from fast CPU and GPU access to each copy of the resource, with minimal API calls needed to synchronize these copies.

**Figure 3-2**Resource storage modes in macOS![Image 4: image: ../Art/ResourceManagement_OSXMemory_2x.png](https://developer.apple.com/library/archive/documentation/3DDrawing/Conceptual/MTLBestPracticesGuide/Art/ResourceManagement_OSXMemory_2x.png)

Important

In macOS, the `Shared` mode is available only for buffers, not textures. Buffer data is usually linear, resulting in simple GPU access patterns. Textures are more complex and their data is usually tiled or swizzled, resulting in more complicated GPU access patterns.

**Buffer Storage Mode (macOS)**

Use the following guidelines to determine the appropriate storage mode for a particular buffer.

*   If the buffer is accessed by the GPU exclusively, choose the `Private` mode. This is a common case for GPU-generated data, such as per-patch tessellation factors.

*   If the buffer is accessed by the CPU exclusively, choose the `Shared` mode. This is a rare case and is usually an intermediary step in a blit operation.

*   If the buffer is accessed by both the CPU and the GPU, as is the case with most vertex data, consider the following points and refer to Table 3-1:

    *   For small-sized data that changes frequently, choose the `Shared` mode. The overhead of copying data to video memory may be more expensive than the overhead of the GPU accessing system memory directly.

    *   For medium-sized data that changes infrequently, choose the `Managed` mode. Always call an appropriate synchronization method after modifying the contents of a managed buffer.

After performing a CPU write, call the `didModifyRange:` method to notify Metal about the specific range of data that was modified; this allows Metal to update only that specific range in the video memory copy.

After encoding a GPU write, encode a blit operation that includes a call to the `synchronizeResource:` method; this allows Metal to update the system memory copy after the associated command buffer has completed execution.

    *   For large-sized data that never changes, choose the `Private` mode. Initialize and populate a source buffer with a `Shared` mode and then blit its data into a destination buffer with a `Private` mode. This is an optimal operation with a one-time cost.

**Table 3-1**Choosing a storage mode for buffer data accessed by both the CPU and the GPU
| Data size | Resource dirtiness | Update frequency | Storage mode |
| --- | --- | --- | --- |
| Small | Full | Every frame | `Shared` |
| Medium | Partial | Every _n_ frames | `Managed` |
| Large | N/A | Once | `Private` (After a blit from a shared source buffer) |

**Texture Storage Mode (macOS)**

In macOS, the default storage mode for textures is `Managed`. Use the following guidelines to determine the appropriate storage mode for a particular texture.

*   If the texture is accessed by the GPU exclusively, choose the `Private` mode. This is a common case for GPU-generated data, such as displayable render targets.

*   If the texture is accessed by the CPU exclusively, choose the `Managed` mode. This is a rare case and is usually an intermediary step in a blit operation.

*   If the texture is initialized once by the CPU and accessed frequently by the GPU, initialize a source texture with a `Managed` mode and then blit its data into a destination texture with a `Private` mode. This is a common case for static textures, such as diffuse maps.

*   If the texture is accessed frequently by both the CPU and GPU, choose the `Managed` mode. This is a common case for dynamic textures, such as image filters. Always call an appropriate synchronization method after modifying the contents of a managed texture.

To perform a CPU write to a specific region of data and simultaneously notify Metal about the change, call either of the following methods. This allows Metal to update only that specific region in the video memory copy.

    *   `replaceRegion:mipmapLevel:withBytes:bytesPerRow:`

    *   `replaceRegion:mipmapLevel:slice:withBytes:bytesPerRow:bytesPerImage:`

After encoding a GPU write, encode a blit operation that includes a call to either of the following methods. This allows Metal to update the system memory copy after the associated command buffer has completed execution.

    *   `synchronizeResource:`

    *   `synchronizeTexture:slice:level:`

### Set Appropriate Texture Usage Flags

Metal can optimize GPU operations for a given texture, based on its intended use. Always declare explicit texture usage options if you know them in advance. Do not rely on the `Unknown` option; although this option provides the most flexibility for your textures, it incurs a significant performance cost. The driver cannot perform any optimizations if it does not know how you intend to use your texture. For a description of available texture usage options, see the `MTLTextureUsage` reference.

Persistent Objects

Triple Buffering

Copyright © 2018 Apple Inc. All rights reserved. Terms of Use | Privacy Policy | Updated: 2017-03-27

## Sending feedback…

## We’re sorry, an error has occurred.

Please try submitting your feedback later.

## Thank you for providing feedback!

Your input helps improve our developer documentation.

## How helpful is this document?

*

 Very helpful   Somewhat helpful   Not helpful  

## How can we improve this document?

- [x]  Fix typos or links  - [x]  Fix incorrect information  - [x]  Add or update code samples  - [x]  Add or update illustrations  - [x]  Add information about...  

*
_* Required information_

To submit a product bug or enhancement request, please visit the Bug Reporter page.

Please read Apple's Unsolicited Idea Submission Policy before you send us your feedback.