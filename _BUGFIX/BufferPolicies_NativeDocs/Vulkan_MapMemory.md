![Image 1: Vulkan White Label](https://docs.vulkan.org/_/Vulkan_Docs.svg)

- [x]  In this component  

Specs

Vulkan API GLSL HLSL Slang

Education

Vulkan Reference Pages Vulkan Feature Descriptions Vulkan Guide Vulkan Samples Vulkan Tutorial GLSL Extensions YouTube More Info at Vulkan.org

Feedback

Report a Problem

Community

Vulkan Discord Reddit Stack Overflow Mastodon

- [x]  

*   Vulkan API Reference Pages
*   Commands
*   M
*   vkMapMemory

# vkMapMemory(3)

## Name

vkMapMemory - Map a memory object into application address space

## C Specification

To retrieve a host virtual address pointer to a region of a mappable memory object, call:

```c
// Provided by VK_VERSION_1_0
VkResult vkMapMemory(
    VkDevice                                    device,
    VkDeviceMemory                              memory,
    VkDeviceSize                                offset,
    VkDeviceSize                                size,
    VkMemoryMapFlags                            flags,
    void**                                      ppData);
```

## Parameters

*   `device` is the logical device that owns the memory.

*   `memory` is the VkDeviceMemory object to be mapped.

*   `offset` is a zero-based byte offset from the beginning of the memory object.

*   `size` is the size of the memory range to map, or VK_WHOLE_SIZE to map from `offset` to the end of the allocation.

*   `flags` is a bitmask of VkMemoryMapFlagBits specifying additional parameters of the memory map operation.

*   `ppData` is a pointer to a `void*` variable in which a host-accessible pointer to the beginning of the mapped range is returned. The value of the returned pointer minus `offset`**must** be aligned to VkPhysicalDeviceLimits::`minMemoryMapAlignment`.

## Description

After a successful call to `vkMapMemory` the memory object `memory` is considered to be currently _host mapped_.

It is an application error to call `vkMapMemory` on a memory object that is already _host mapped_.

`vkMapMemory` will fail if the implementation is unable to allocate an appropriately sized contiguous virtual address range, e.g. due to virtual address space fragmentation or platform limits. In such cases, `vkMapMemory`**must** return VK_ERROR_MEMORY_MAP_FAILED. The application **can** improve the likelihood of success by reducing the size of the mapped range and/or removing unneeded mappings using vkUnmapMemory.

`vkMapMemory` does not check whether the device memory is currently in use before returning the host-accessible pointer. The application **must** guarantee that any previously submitted command that writes to this range has completed before the host reads from or writes to that range, and that any previously submitted command that reads from that range has completed before the host writes to that region (see here for details on fulfilling such a guarantee). If the device memory was allocated without the VK_MEMORY_PROPERTY_HOST_COHERENT_BIT set, these guarantees **must** be made for an extended range: the application **must** round down the start of the range to the nearest multiple of VkPhysicalDeviceLimits::`nonCoherentAtomSize`, and round the end of the range up to the nearest multiple of VkPhysicalDeviceLimits::`nonCoherentAtomSize`.

While a range of device memory is host mapped, the application is responsible for synchronizing both device and host access to that memory range.

It is important for the application developer to become meticulously familiar with all of the mechanisms described in the chapter on Synchronization and Cache Control as they are crucial to maintaining memory access ordering.

Calling `vkMapMemory` is equivalent to calling vkMapMemory2 with an empty `pNext` chain.

Valid Usage

*   VUID-vkMapMemory-memory-00678  
`memory`**must** not be currently host mapped

*   VUID-vkMapMemory-offset-00679  
`offset`**must** be less than the size of `memory`

*   VUID-vkMapMemory-size-00680  
 If `size` is not equal to VK_WHOLE_SIZE, `size`**must** be greater than `0`

*   VUID-vkMapMemory-size-00681  
 If `size` is not equal to VK_WHOLE_SIZE, `size`**must** be less than or equal to the size of the `memory` minus `offset`

*   VUID-vkMapMemory-memory-00682  
`memory`**must** have been created with a memory type that reports VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT

*   VUID-vkMapMemory-memory-00683  
`memory`**must** not have been allocated with multiple instances

*   VUID-vkMapMemory-flags-09568  
VK_MEMORY_MAP_PLACED_BIT_EXT**must** not be set in `flags`

Valid Usage (Implicit)

*   VUID-vkMapMemory-device-parameter  
`device`**must** be a valid VkDevice handle

*   VUID-vkMapMemory-memory-parameter  
`memory`**must** be a valid VkDeviceMemory handle

*   VUID-vkMapMemory-flags-parameter  
 If `flags` is not `0`, `flags`**must** be a valid combination of VkMemoryMapFlagBits values

*   VUID-vkMapMemory-ppData-parameter  
`ppData`**must** be a valid pointer to a pointer value

*   VUID-vkMapMemory-memory-parent  
`memory`**must** have been created, allocated, or retrieved from `device`

Host Synchronization

*   Host access to `memory`**must** be externally synchronized

Return Codes

Success

*   VK_SUCCESS

Failure

*   VK_ERROR_MEMORY_MAP_FAILED

*   VK_ERROR_OUT_OF_DEVICE_MEMORY

*   VK_ERROR_OUT_OF_HOST_MEMORY

*   VK_ERROR_UNKNOWN

*   VK_ERROR_VALIDATION_FAILED

## See Also

VK_VERSION_1_0, VkDevice, VkDeviceMemory, `VkDeviceSize`, VkMemoryMapFlags

## Document Notes

For more information, see the Vulkan Specification.

This page is extracted from the Vulkan Specification. Fixes and changes should be made to the Specification, not directly.

Manage cookie preferences

#### **Cookie Policy**

Clicking 'Accept All Cookies' means you agree to our cookie use and data sharing as described in our privacy policy under 'Cookies and Tracking Technologies'.

Manage cookie options

Necessary Cookies Only Accept All Cookies